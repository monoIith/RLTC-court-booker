using System.Text.Json;
using System.Diagnostics;
using RockcliffeCourtBooker.Automation;
using RockcliffeCourtBooker.Automation.Contracts;
using RockcliffeCourtBooker.Core;
using RockcliffeCourtBooker.Infrastructure;
using RockcliffeCourtBooker.Notifications;
using RockcliffeCourtBooker.Scheduling;
using RockcliffeCourtBooker.Worker.Contracts;
using static RockcliffeCourtBooker.WorkerLogic.WorkerAttemptFinalizer;
using AutomationSensitiveDataRedactor = RockcliffeCourtBooker.Automation.Security.SensitiveDataRedactor;

namespace RockcliffeCourtBooker.Worker;

internal static class Program
{
    // Task Scheduler enforces 20 minutes. Stop active work one minute earlier so a
    // cancelled attempt still has time to persist its terminal safety record.
    private static readonly TimeSpan MaximumRuntime = TimeSpan.FromMinutes(19);
    private static readonly TimeSpan TermsAuthorizationLifetime = TimeSpan.FromDays(30);

    public static async Task<int> Main(string[] args)
    {
        if (args is ["--unregister-notifications"])
        {
            return UnregisterNotificationActivation();
        }

        if (args is ["--check-notifications"])
        {
            return WindowsBookingNotificationService.IsSupported() ? 0 : 4;
        }

        if (OperatingSystem.IsWindows() &&
            (args.Length == 0 || NotificationActivationLaunch.IsActivationLaunch(args)))
        {
            return await HandleNotificationActivationAsync();
        }

        var isScheduledRun = args is ["run-due", "--scheduled"];
        string? requestPath = null;
        string? resultPath = null;
        if (!isScheduledRun &&
            !TryParseArguments(args, out requestPath, out resultPath, out var argumentError))
        {
            await Console.Error.WriteLineAsync(argumentError);
            await Console.Error.WriteLineAsync("Usage: RockcliffeCourtBooker.Worker run-due --scheduled");
            await Console.Error.WriteLineAsync("   or: RockcliffeCourtBooker.Worker --request <absolute-json-path> [--result <absolute-json-path>]");
            await Console.Error.WriteLineAsync("   or: RockcliffeCourtBooker.Worker --unregister-notifications");
            await Console.Error.WriteLineAsync("   or: RockcliffeCourtBooker.Worker --check-notifications");
            return 2;
        }

        using var cancellation = new CancellationTokenSource(MaximumRuntime);
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        BookingWorkerResult result;
        try
        {
            if (isScheduledRun)
            {
                result = await RunDueScheduledAsync(cancellation.Token);
            }
            else
            {
                var request = await BookingWorkerRequestLoader.LoadAsync(requestPath!, cancellation.Token);
                result = await RunDirectRequestAsync(request, cancellation.Token);
            }
        }
        catch (WorkerInputException exception)
        {
            result = new BookingWorkerResult
            {
                AttemptId = Guid.Empty,
                Outcome = WorkerOutcome.InvalidRequest,
                Message = exception.Message,
            };
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            result = new BookingWorkerResult
            {
                AttemptId = Guid.Empty,
                Outcome = WorkerOutcome.AutomationFailed,
                Message = "The worker was cancelled or exceeded its 19-minute active-work limit.",
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            result = new BookingWorkerResult
            {
                AttemptId = Guid.Empty,
                Outcome = WorkerOutcome.AutomationFailed,
                Message = $"The worker stopped safely: {new AutomationSensitiveDataRedactor().Redact(exception.Message)}",
            };
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }

        await WriteResultAsync(result, resultPath, CancellationToken.None);
        TryShowNotification(result);
        return ExitCode(result);
    }

    private static async Task<BookingWorkerResult> RunDueScheduledAsync(CancellationToken cancellationToken)
    {
        var paths = AppPaths.CreateDefault();
        paths.EnsureDirectoriesExist();
        await using var repository = new SqliteBookingRepository(paths.DatabasePath);
        await repository.InitializeAsync(cancellationToken);

        var settings = await repository.GetSettingsAsync(cancellationToken);
        if (!settings.UnattendedSubmissionEnabled)
        {
            return new BookingWorkerResult
            {
                AttemptId = Guid.Empty,
                Outcome = WorkerOutcome.Completed,
                Message = "Unattended submission is disabled; no booking was attempted.",
            };
        }

        var schedule = new BookingScheduleService();
        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, schedule.TimeZone);
        var targetDate = DateOnly.FromDateTime(localNow.DateTime).AddDays(3);
        var rules = await repository.GetRulesAsync(cancellationToken);
        var dueRules = rules
            .Where(rule => rule.Enabled)
            .Where(rule => rule.Kind == BookingRuleKind.OneTime
                ? rule.TargetDate == targetDate
                : rule.Weekdays.Contains(targetDate.DayOfWeek))
            .ToArray();
        if (dueRules.Length == 0)
        {
            return new BookingWorkerResult
            {
                AttemptId = Guid.Empty,
                Outcome = WorkerOutcome.Completed,
                Message = $"No enabled booking rule is due for {targetDate:yyyy-MM-dd}.",
            };
        }

        if (dueRules.Length > 1)
        {
            return new BookingWorkerResult
            {
                AttemptId = Guid.Empty,
                Outcome = WorkerOutcome.InvalidRequest,
                Message = $"Multiple enabled rules claim {targetDate:yyyy-MM-dd}; no booking was attempted.",
            };
        }

        if (await repository.HasSubmissionRiskAsync(targetDate, cancellationToken))
        {
            return SubmissionRiskResult(Guid.Empty);
        }

        var rule = dueRules[0];
        var attemptId = Guid.NewGuid();
        await using var lease = await repository.TryAcquireOccurrenceLockAsync(
            targetDate,
            $"{attemptId:D}:worker:{Environment.ProcessId}",
            MaximumRuntime,
            cancellationToken);
        if (lease is null)
        {
            return new BookingWorkerResult
            {
                AttemptId = attemptId,
                Outcome = WorkerOutcome.AlreadyRunning,
                Message = "Another worker owns the database lease for this target date.",
            };
        }

        // Recheck under the persistent lease. This closes the gap between the first
        // history query and lease acquisition without relying on a bounded history page.
        if (await repository.HasSubmissionRiskAsync(targetDate, cancellationToken))
        {
            return SubmissionRiskResult(Guid.Empty);
        }

        var window = schedule.GetWindow(targetDate);
        var attempt = new BookingAttempt
        {
            Id = attemptId,
            RuleId = rule.Id,
            TargetDate = targetDate,
            ScheduledForUtc = window.WorkerStartsAtUtc,
            StartedAtUtc = DateTimeOffset.UtcNow,
            Status = BookingAttemptStatus.Running,
        };
        await repository.AddAttemptAsync(attempt, cancellationToken);

        return await RunAndFinalizeAttemptAsync(
            repository,
            attempt,
            async token =>
            {
                WindowsSystemHealth? health = null;
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        health = await new WindowsSystemHealthService().CheckAsync(token);
                    }
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException)
                {
                    // A failed clock query is unhealthy and must prevent unattended browser automation.
                }

                if (health is null || !health.CanScheduleUnattended)
                {
                    return InvalidRequestResult(
                        attemptId,
                        "Unattended booking requires Windows Eastern Standard Time and a synchronized Windows clock.");
                }

                if (string.IsNullOrWhiteSpace(settings.ExternalConfigurationPath))
                {
                    return new BookingWorkerResult
                    {
                        AttemptId = attemptId,
                        Outcome = WorkerOutcome.ConfigurationFailed,
                        Message = "No external account/player configuration path is saved.",
                    };
                }

                if (!IsTermsAuthorizationCurrent(rule.TermsAuthorizedAtUtc, DateTimeOffset.UtcNow))
                {
                    return InvalidRequestResult(
                        attemptId,
                        "The recorded Matchpoint terms authorization is missing or older than 30 days. Re-authorize the rule before submitting.");
                }

                return await RunAsync(
                    CreateRequest(
                        settings,
                        rule,
                        targetDate,
                        attemptId,
                        paths.DiagnosticsDirectory,
                        userInitiated: false),
                    token);
            },
            cancellationToken);
    }

    private static async Task<BookingWorkerResult> RunDirectRequestAsync(
        BookingWorkerRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = WorkerRequestValidator.Validate(request);
        if (validationErrors.Count > 0)
        {
            return InvalidRequestResult(request.AttemptId, string.Join(" ", validationErrors));
        }

        var paths = AppPaths.CreateDefault();
        paths.EnsureDirectoriesExist();
        await using var repository = new SqliteBookingRepository(paths.DatabasePath);
        await repository.InitializeAsync(cancellationToken);

        await using var lease = await repository.TryAcquireOccurrenceLockAsync(
            request.TargetDate,
            $"{request.AttemptId:D}:worker:{Environment.ProcessId}",
            MaximumRuntime,
            cancellationToken);
        if (lease is null)
        {
            return new BookingWorkerResult
            {
                AttemptId = request.AttemptId,
                Outcome = WorkerOutcome.AlreadyRunning,
                Message = "Another worker owns the database lease for this target date.",
            };
        }

        var existingAttempt = await repository.GetAttemptAsync(request.AttemptId, cancellationToken);
        if (existingAttempt is not null &&
            (existingAttempt.Status != BookingAttemptStatus.Running ||
             existingAttempt.RuleId != request.RuleId ||
             existingAttempt.TargetDate != request.TargetDate ||
             existingAttempt.IsDryRun != (request.Mode != AutomationMode.Submit)))
        {
            return InvalidRequestResult(
                request.AttemptId,
                "The persisted attempt does not match this request or is no longer running.");
        }

        if (request.Mode == AutomationMode.Submit &&
            await repository.HasOtherSubmissionRiskAsync(
                request.TargetDate,
                request.AttemptId,
                cancellationToken))
        {
            var riskResult = SubmissionRiskResult(request.AttemptId);
            if (existingAttempt is null)
            {
                return riskResult;
            }

            return await RunAndFinalizeAttemptAsync(
                repository,
                existingAttempt,
                _ => Task.FromResult(riskResult),
                cancellationToken);
        }

        var attempt = existingAttempt;
        if (attempt is null)
        {
            var startedAt = DateTimeOffset.UtcNow;
            attempt = new BookingAttempt
            {
                Id = request.AttemptId,
                RuleId = request.RuleId,
                TargetDate = request.TargetDate,
                ScheduledForUtc = startedAt,
                StartedAtUtc = startedAt,
                Status = BookingAttemptStatus.Running,
                IsDryRun = request.Mode != AutomationMode.Submit,
            };
            await repository.AddAttemptAsync(attempt, cancellationToken);
        }

        return await RunAndFinalizeAttemptAsync(
            repository,
            attempt,
            async token =>
            {
                var rule = await repository.GetRuleAsync(request.RuleId, token);
                if (rule is null || !RuleMatchesRequest(rule, request))
                {
                    return InvalidRequestResult(
                        request.AttemptId,
                        "The request no longer exactly matches its persisted booking rule.");
                }

                if (request.Mode == AutomationMode.Submit &&
                    !IsTermsAuthorizationCurrent(rule.TermsAuthorizedAtUtc, DateTimeOffset.UtcNow))
                {
                    return InvalidRequestResult(
                        request.AttemptId,
                        "The recorded Matchpoint terms authorization is missing or older than 30 days. Re-authorize the rule before submitting.");
                }

                return await RunAsync(request, token);
            },
            cancellationToken);
    }

    private static BookingWorkerRequest CreateRequest(
        AppSettings settings,
        BookingRule rule,
        DateOnly targetDate,
        Guid attemptId,
        string diagnosticsDirectory,
        bool userInitiated) =>
        new()
        {
            AttemptId = attemptId,
            RuleId = rule.Id,
            AccountConfigurationPath = settings.ExternalConfigurationPath!,
            TargetDate = targetDate,
            OrderedStartTimes = rule.StartTimes,
            BookingKind = rule.BookingType == BookingType.Singles
                ? BookingKind.Singles
                : BookingKind.Doubles,
            DurationMinutes = rule.DurationMinutes,
            PlayerIds = rule.PlayerIds,
            CourtOrder = rule.CourtOrder,
            Mode = AutomationMode.Submit,
            TermsAuthorized = true,
            VisibleBrowser = settings.VisibleBrowserByDefault,
            UserInitiated = userInitiated,
            DiagnosticsDirectory = diagnosticsDirectory,
        };

    private static bool RuleMatchesRequest(BookingRule rule, BookingWorkerRequest request)
    {
        var expectedKind = rule.BookingType == BookingType.Singles
            ? BookingKind.Singles
            : BookingKind.Doubles;
        var occurrenceMatches = rule.Kind == BookingRuleKind.OneTime
            ? rule.TargetDate == request.TargetDate
            : rule.Weekdays.Contains(request.TargetDate.DayOfWeek);
        return rule.Id == request.RuleId &&
               occurrenceMatches &&
               expectedKind == request.BookingKind &&
               rule.DurationMinutes == request.DurationMinutes &&
               rule.StartTimes.SequenceEqual(request.OrderedStartTimes) &&
               rule.PlayerIds.SequenceEqual(request.PlayerIds, StringComparer.OrdinalIgnoreCase) &&
               rule.CourtOrder.SequenceEqual(request.CourtOrder);
    }

    private static bool IsTermsAuthorizationCurrent(
        DateTimeOffset? authorizedAtUtc,
        DateTimeOffset nowUtc) =>
        authorizedAtUtc is { } authorized &&
        authorized <= nowUtc &&
        nowUtc - authorized <= TermsAuthorizationLifetime;

    private static BookingWorkerResult InvalidRequestResult(Guid attemptId, string message) =>
        new()
        {
            AttemptId = attemptId,
            Outcome = WorkerOutcome.InvalidRequest,
            Message = message,
        };

    private static BookingWorkerResult SubmissionRiskResult(Guid attemptId) =>
        new()
        {
            AttemptId = attemptId,
            Outcome = WorkerOutcome.AlreadyRunning,
            Message = "A prior running, successful, or uncertain attempt blocks this target date.",
        };

    private static async Task<BookingWorkerResult> RunAsync(
        BookingWorkerRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = WorkerRequestValidator.Validate(request);
        if (validationErrors.Count > 0)
        {
            return new BookingWorkerResult
            {
                AttemptId = request.AttemptId,
                Outcome = WorkerOutcome.InvalidRequest,
                Message = string.Join(" ", validationErrors),
            };
        }

        ExternalConfigurationLoadResult loaded;
        try
        {
            loaded = await new ExternalConfigurationLoader().LoadAsync(
                request.AccountConfigurationPath,
                cancellationToken);
        }
        catch (ExternalConfigurationException exception)
        {
            return new BookingWorkerResult
            {
                AttemptId = request.AttemptId,
                Outcome = WorkerOutcome.ConfigurationFailed,
                Message = $"Account/player configuration failed validation ({exception.Code}).",
            };
        }

        var redactor = new AutomationSensitiveDataRedactor(
            loaded.Configuration.Account.Password,
            loaded.Configuration.Account.Username);
        var paths = AppPaths.CreateDefault();
        paths.EnsureDirectoriesExist();
        var log = new SafeWorkerLog(paths.LogsDirectory, redactor);
        log.DeleteExpired();
        log.Write($"Attempt {request.AttemptId:D} started for target {request.TargetDate:yyyy-MM-dd} in {request.Mode} mode.");

        var playerLookup = loaded.Configuration.Players.ToDictionary(
            player => player.Id,
            player => player.DisplayName,
            StringComparer.OrdinalIgnoreCase);
        var playerNames = new List<string>(request.PlayerIds.Count);
        foreach (var playerId in request.PlayerIds)
        {
            if (!playerLookup.TryGetValue(playerId, out var playerName))
            {
                var missingResult = new BookingWorkerResult
                {
                    AttemptId = request.AttemptId,
                    Outcome = WorkerOutcome.ConfigurationFailed,
                    Message = "A requested player ID is not present in the current external configuration.",
                };
                log.Write($"Attempt {request.AttemptId:D} stopped: configured player ID was not found.");
                return missingResult;
            }

            playerNames.Add(playerName);
        }

        var schedule = new BookingScheduleService();
        var window = schedule.GetWindow(request.TargetDate);
        if (!request.UserInitiated &&
            request.Mode == AutomationMode.Submit &&
            DateTimeOffset.UtcNow >= window.UnattendedCutoffAtUtc)
        {
            var missedResult = new BookingWorkerResult
            {
                AttemptId = request.AttemptId,
                Outcome = WorkerOutcome.MissedWindow,
                Message = "The unattended 8:15 AM booking cutoff has passed; no browser was opened.",
            };
            log.Write($"Attempt {request.AttemptId:D} stopped because the unattended window was missed.");
            return missedResult;
        }

        using var targetLock = TargetDateLock.TryAcquire(request.TargetDate);
        if (!targetLock.Acquired)
        {
            var lockedResult = new BookingWorkerResult
            {
                AttemptId = request.AttemptId,
                Outcome = WorkerOutcome.AlreadyRunning,
                Message = "Another worker is already handling this target date.",
            };
            log.Write($"Attempt {request.AttemptId:D} stopped because the target-date lock is held.");
            return lockedResult;
        }

        var automationRequest = new MatchpointBookingRequest
        {
            AttemptId = request.AttemptId.ToString("D"),
            Credentials = new MatchpointCredentials
            {
                Username = loaded.Configuration.Account.Username,
                Password = loaded.Configuration.Account.Password,
            },
            AccountMemberDisplayName = loaded.Configuration.Account.MemberName,
            TargetDate = request.TargetDate,
            OrderedStartTimes = request.OrderedStartTimes,
            BookingKind = request.BookingKind,
            DurationMinutes = request.DurationMinutes,
            PartnerDisplayNames = playerNames,
            CourtOrder = request.CourtOrder,
            Mode = request.Mode,
            TermsAuthorized = request.TermsAuthorized,
            Headless = !request.VisibleBrowser,
            BookingOpensAtUtc = window.OpensAtUtc,
            SubmissionCutoffAtUtc = !request.UserInitiated && request.Mode == AutomationMode.Submit
                ? window.UnattendedCutoffAtUtc
                : null,
            // The browser executable is an installation concern, not an IPC choice.
            // Playwright resolves only the bundled runtime configured for the app.
            ChromiumExecutablePath = null,
            // Never trust an IPC request to choose a recursively-maintained directory.
            // Diagnostics retention is confined to the application's local data root.
            DiagnosticsDirectory = paths.DiagnosticsDirectory,
        };

        CancellationTokenSource? cutoffCancellation = null;
        CancellationTokenSource? linkedCancellation = null;
        var automationCancellationToken = cancellationToken;
        var enforceUnattendedCutoff = !request.UserInitiated && request.Mode == AutomationMode.Submit;
        if (enforceUnattendedCutoff)
        {
            var remaining = window.UnattendedCutoffAtUtc - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return new BookingWorkerResult
                {
                    AttemptId = request.AttemptId,
                    Outcome = WorkerOutcome.MissedWindow,
                    Message = "The unattended 8:15 AM booking cutoff was reached before browser automation began.",
                };
            }

            cutoffCancellation = new CancellationTokenSource();
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                cutoffCancellation.Token);
            automationCancellationToken = linkedCancellation.Token;

            // The worker's 19-minute lifetime will cancel first when the cutoff is
            // farther away, avoiding an unnecessarily long timer.
            if (remaining <= MaximumRuntime)
            {
                cutoffCancellation.CancelAfter(remaining);
            }
        }

        BookingAutomationResult automationResult;
        var cutoffReached = false;
        try
        {
            automationResult = await new PlaywrightBookingAutomation().ExecuteAsync(
                automationRequest,
                automationCancellationToken);
        }
        finally
        {
            cutoffReached = cutoffCancellation?.IsCancellationRequested == true;
            linkedCancellation?.Dispose();
            cutoffCancellation?.Dispose();
        }

        if (IsPreSubmissionCutoffCancellation(
                enforceUnattendedCutoff,
                cutoffReached,
                DateTimeOffset.UtcNow,
                window.UnattendedCutoffAtUtc,
                automationResult.Status))
        {
            var missedResult = new BookingWorkerResult
            {
                AttemptId = request.AttemptId,
                Outcome = WorkerOutcome.MissedWindow,
                Message = "The unattended 8:15 AM booking cutoff was reached before final submission began; no booking was made.",
                AutomationResult = automationResult,
            };
            log.Write($"Attempt {request.AttemptId:D} stopped at the unattended cutoff before final submission.");
            return missedResult;
        }
        var successful = automationResult.Status is
            BookingAutomationStatus.Succeeded or
            BookingAutomationStatus.ReadOnlyComplete or
            BookingAutomationStatus.DryRunComplete;
        var result = new BookingWorkerResult
        {
            AttemptId = request.AttemptId,
            Outcome = successful ? WorkerOutcome.Completed : WorkerOutcome.AutomationFailed,
            Message = automationResult.Message,
            AutomationResult = automationResult,
        };
        log.Write($"Attempt {request.AttemptId:D} completed with automation status {automationResult.Status}.");
        return result;
    }

    private static bool TryParseArguments(
        string[] args,
        out string? requestPath,
        out string? resultPath,
        out string error)
    {
        requestPath = null;
        resultPath = null;
        error = string.Empty;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, "--request", StringComparison.Ordinal) && index + 1 < args.Length)
            {
                requestPath = args[++index];
            }
            else if (string.Equals(argument, "--result", StringComparison.Ordinal) && index + 1 < args.Length)
            {
                resultPath = args[++index];
            }
            else
            {
                error = $"Unknown or incomplete argument: {argument}";
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(requestPath) || !Path.IsPathFullyQualified(requestPath))
        {
            error = "An absolute --request path is required.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(resultPath) && !Path.IsPathFullyQualified(resultPath))
        {
            error = "--result must be an absolute path when supplied.";
            return false;
        }

        return true;
    }

    private static async Task WriteResultAsync(
        BookingWorkerResult result,
        string? resultPath,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(result, WorkerJson.Options);
        await Console.Out.WriteLineAsync(json);
        if (string.IsNullOrWhiteSpace(resultPath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(resultPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(resultPath, json, cancellationToken);
    }

    private static void TryShowNotification(BookingWorkerResult result)
    {
        if (!OperatingSystem.IsWindows() || result.AttemptId == Guid.Empty)
        {
            return;
        }

        try
        {
            using var notifications = new WindowsBookingNotificationService();
            notifications.Register();
            notifications.Show(new BookingNotification(
                result.AttemptId,
                result.AutomationResult?.Status == BookingAutomationStatus.Succeeded,
                result.AutomationResult?.Status == BookingAutomationStatus.Succeeded
                    ? "Court booked"
                    : "Court booking update",
                result.Message));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Notification registration can fail independently of the booking result,
            // but it must leave a sanitized local diagnostic instead of disappearing.
            try
            {
                var paths = AppPaths.CreateDefault();
                paths.EnsureDirectoriesExist();
                new SafeWorkerLog(paths.LogsDirectory, new AutomationSensitiveDataRedactor()).Write(
                    $"Attempt {result.AttemptId:D} completed, but Windows app notification delivery was unavailable.");
            }
            catch (Exception loggingException) when (loggingException is not OutOfMemoryException)
            {
                _ = exception;
                _ = loggingException;
            }
        }
    }

    private static int UnregisterNotificationActivation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        try
        {
            WindowsBookingNotificationService.UnregisterAll();
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine("Failed to unregister Windows notification activation.");
            return 4;
        }
    }

    private static async Task<int> HandleNotificationActivationAsync()
    {
        var activatedAttempt = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var notifications = new WindowsBookingNotificationService();
            notifications.Register(attemptId => activatedAttempt.TrySetResult(attemptId));
            var completed = await Task.WhenAny(
                activatedAttempt.Task,
                Task.Delay(TimeSpan.FromSeconds(15)));
            if (completed != activatedAttempt.Task)
            {
                return 0;
            }

            var appExecutable = Path.Combine(AppContext.BaseDirectory, "RockcliffeCourtBooker.exe");
            if (!File.Exists(appExecutable))
            {
                return 4;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = appExecutable,
                UseShellExecute = true,
            };
            startInfo.ArgumentList.Add("--history");
            startInfo.ArgumentList.Add((await activatedAttempt.Task).ToString("D"));
            Process.Start(startInfo);
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = exception;
            return 4;
        }
    }

    private static int ExitCode(BookingWorkerResult result)
    {
        if (result.Outcome == WorkerOutcome.Completed)
        {
            return 0;
        }

        if (result.Outcome is WorkerOutcome.InvalidRequest or WorkerOutcome.ConfigurationFailed)
        {
            return 2;
        }

        if (result.Outcome is WorkerOutcome.MissedWindow or WorkerOutcome.AlreadyRunning)
        {
            return 3;
        }

        return result.AutomationResult?.Status == BookingAutomationStatus.SubmissionUncertain ? 5 : 4;
    }
}
