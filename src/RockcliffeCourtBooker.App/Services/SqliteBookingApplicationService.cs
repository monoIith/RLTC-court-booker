using RockcliffeCourtBooker.App.Models;
using RockcliffeCourtBooker.Core;
using RockcliffeCourtBooker.Infrastructure;
using CoreBookingType = RockcliffeCourtBooker.Core.BookingType;
using CoreRuleKind = RockcliffeCourtBooker.Core.BookingRuleKind;
using UiBookingType = RockcliffeCourtBooker.App.Models.BookingType;
using UiRuleKind = RockcliffeCourtBooker.App.Models.RuleKind;

namespace RockcliffeCourtBooker.App.Services;

public sealed class SqliteBookingApplicationService : IBookingApplicationService, IHistoryApplicationService
{
    private static readonly TimeSpan WorkerLeaseDuration = TimeSpan.FromMinutes(22);
    private static readonly TimeSpan TermsAuthorizationLifetime = TimeSpan.FromDays(30);
    private readonly IBookingRepository repository;
    private readonly IExternalConfigurationLoader configurationLoader;
    private readonly BookingRuleValidator validator;
    private readonly BookingScheduleService scheduleService;
    private readonly WindowsSchedulerCoordinator scheduler;
    private readonly WorkerProcessClient worker;
    private readonly AppPaths paths;

    public SqliteBookingApplicationService(
        IBookingRepository repository,
        IExternalConfigurationLoader configurationLoader,
        AppPaths paths,
        string workerExecutable)
    {
        this.repository = repository;
        this.configurationLoader = configurationLoader;
        this.paths = paths;
        validator = new BookingRuleValidator();
        scheduleService = new BookingScheduleService();
        scheduler = new WindowsSchedulerCoordinator(repository, workerExecutable);
        worker = new WorkerProcessClient(
            workerExecutable,
            Path.Combine(paths.DataDirectory, "worker-requests"));
    }

    public async Task<bool> GetUnattendedSubmissionEnabledAsync(CancellationToken cancellationToken = default) =>
        (await repository.GetSettingsAsync(cancellationToken)).UnattendedSubmissionEnabled;

    public async Task<OperationResult> SetUnattendedSubmissionEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var settings = await repository.GetSettingsAsync(cancellationToken);
        if (enabled)
        {
            var configurationFailure = await ValidateSavedConfigurationAsync(settings, cancellationToken);
            if (configurationFailure is not null)
            {
                if (settings.UnattendedSubmissionEnabled)
                {
                    await DisableUnattendedSubmissionAsync(cancellationToken);
                }

                return configurationFailure;
            }
        }

        await repository.SaveSettingsAsync(
            settings with
            {
                UnattendedSubmissionEnabled = enabled,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            },
            cancellationToken);
        var sync = await scheduler.SyncAsync(cancellationToken);
        if (sync.IsSuccess)
        {
            return OperationResult.Success(enabled
                ? "Unattended submission is enabled and Windows Task Scheduler is synchronized."
                : "Unattended submission is disabled. Scheduled launches will make no website changes.");
        }

        if (enabled)
        {
            await DisableUnattendedSubmissionAsync(cancellationToken);
            return OperationResult.Failure($"{sync.Message} Unattended submission remains disabled.");
        }

        return OperationResult.Success(
            "Unattended submission is disabled. Windows may retain a harmless stale launch, but the worker will exit without booking.");
    }

    public async Task<IReadOnlyList<ScheduleSummary>> GetSchedulesAsync(
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var rules = await repository.GetRulesAsync(cancellationToken);
        return rules
            .Select(rule => ToSummary(rule, now))
            .OrderBy(static schedule => schedule.NextAttempt ?? DateTimeOffset.MaxValue)
            .ThenBy(static schedule => schedule.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async Task<ScheduleSummary?> GetScheduleAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var rule = await repository.GetRuleAsync(id, cancellationToken);
        return rule is null ? null : ToSummary(rule, DateTimeOffset.UtcNow);
    }

    public async Task<OperationResult> SaveScheduleAsync(
        BookingRuleDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var settings = await repository.GetSettingsAsync(cancellationToken);
        var loaded = await TryLoadConfigurationAsync(settings.ExternalConfigurationPath, cancellationToken);
        if (loaded.Configuration is null)
        {
            return loaded.Failure!;
        }

        var playerIds = loaded.Configuration.Players
            .Select(static player => player.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (draft.PlayerIds.Any(playerId => !playerIds.Contains(playerId)))
        {
            return OperationResult.Failure(
                "One or more selected players are no longer present in the current external configuration.");
        }

        var existingRules = await repository.GetRulesAsync(cancellationToken);
        var conflict = FindRuleConflict(draft, existingRules);
        if (conflict is not null)
        {
            return OperationResult.Failure(conflict);
        }

        BookingRule? existing = null;
        if (draft.ExistingId is { } existingId)
        {
            existing = existingRules.FirstOrDefault(rule => rule.Id == existingId);
            if (existing is null)
            {
                return OperationResult.Failure("The schedule being edited no longer exists.");
            }
        }

        var now = DateTimeOffset.UtcNow;
        var rule = new BookingRule
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            Name = draft.Name,
            Kind = draft.Kind == UiRuleKind.OneTime ? CoreRuleKind.OneTime : CoreRuleKind.Weekly,
            TargetDate = draft.TargetDate,
            Weekdays = draft.Weekdays.ToArray(),
            StartTimes = draft.StartTimes.ToArray(),
            BookingType = draft.BookingType == UiBookingType.Singles
                ? CoreBookingType.Singles
                : CoreBookingType.Doubles,
            DurationMinutes = draft.DurationMinutes,
            PlayerIds = draft.PlayerIds.ToArray(),
            CourtOrder = draft.CourtOrder.ToArray(),
            Enabled = existing?.Enabled ?? true,
            TermsAuthorizedAtUtc = draft.TermsAuthorized ? draft.TermsAuthorizedAt ?? now : null,
            CreatedAtUtc = existing?.CreatedAtUtc ?? now,
            UpdatedAtUtc = now,
        };
        var validation = validator.Validate(rule);
        if (!validation.IsValid)
        {
            return OperationResult.Failure(string.Join(" ", validation.Issues.Select(static issue => issue.Message)));
        }

        try
        {
            await repository.SaveRuleAsync(rule, cancellationToken);
        }
        catch (ScheduleConflictException exception)
        {
            return OperationResult.Failure(exception.Message);
        }

        var schedulerResult = await SynchronizeOrDisableAsync(cancellationToken);
        var action = existing is null ? "Schedule saved." : "Schedule updated.";
        return schedulerResult.IsSuccess
            ? OperationResult.Success(action)
            : OperationResult.Success($"{action} {schedulerResult.Message} Unattended submission was disabled for safety.");
    }

    public async Task<OperationResult> SetScheduleEnabledAsync(
        Guid id,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var existing = await repository.GetRuleAsync(id, cancellationToken);
        if (existing is null)
        {
            return OperationResult.Failure("The selected schedule no longer exists.");
        }

        var allRules = await repository.GetRulesAsync(cancellationToken);
        if (enabled)
        {
            var draft = ToDraft(existing);
            var conflict = FindRuleConflict(draft, allRules);
            if (conflict is not null)
            {
                return OperationResult.Failure(conflict);
            }
        }

        try
        {
            await repository.SaveRuleAsync(
                existing with { Enabled = enabled, UpdatedAtUtc = DateTimeOffset.UtcNow },
                cancellationToken);
        }
        catch (ScheduleConflictException exception)
        {
            return OperationResult.Failure(exception.Message);
        }

        var schedulerResult = await SynchronizeOrDisableAsync(cancellationToken);
        var action = enabled ? "Schedule enabled." : "Schedule paused.";
        return schedulerResult.IsSuccess
            ? OperationResult.Success(action)
            : OperationResult.Success($"{action} {schedulerResult.Message} Unattended submission was disabled for safety.");
    }

    public async Task<OperationResult> DeleteScheduleAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (!await repository.DeleteRuleAsync(id, cancellationToken))
        {
            return OperationResult.Failure("The selected schedule no longer exists.");
        }

        var schedulerResult = await SynchronizeOrDisableAsync(cancellationToken);
        return schedulerResult.IsSuccess
            ? OperationResult.Success("Schedule deleted.")
            : OperationResult.Success(
                $"Schedule deleted. {schedulerResult.Message} Unattended submission was disabled for safety.");
    }

    public Task<OperationResult> InspectNowAsync(Guid id, CancellationToken cancellationToken = default) =>
        RunNowAsync(id, WorkerAutomationMode.ReadOnly, cancellationToken);

    public Task<OperationResult> DryRunAsync(Guid id, CancellationToken cancellationToken = default) =>
        RunNowAsync(id, WorkerAutomationMode.DryRun, cancellationToken);

    public async Task<ManualBookingTarget> GetBookNowTargetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var rule = await repository.GetRuleAsync(id, cancellationToken);
        if (rule is null)
        {
            return new ManualBookingTarget(false, null, "The selected schedule no longer exists.");
        }

        var resolved = ResolveBookNowTarget(rule, DateTimeOffset.UtcNow);
        return resolved.TargetDate is { } target
            ? new ManualBookingTarget(
                true,
                target,
                $"The currently opening occurrence is {target:dddd, MMMM d, yyyy}.")
            : new ManualBookingTarget(false, null, resolved.FailureMessage!);
    }

    public Task<OperationResult> BookNowAsync(
        Guid id,
        DateOnly targetDate,
        CancellationToken cancellationToken = default) =>
        RunNowAsync(id, WorkerAutomationMode.Submit, cancellationToken, targetDate);

    private async Task<OperationResult> RunNowAsync(
        Guid id,
        WorkerAutomationMode mode,
        CancellationToken cancellationToken,
        DateOnly? requestedTargetDate = null)
    {
        var rule = await repository.GetRuleAsync(id, cancellationToken);
        if (rule is null)
        {
            return OperationResult.Failure("The selected schedule no longer exists.");
        }

        var targetResult = requestedTargetDate is { } requested
            ? ValidateRequestedBookNowTarget(rule, requested, DateTimeOffset.UtcNow)
            : ResolveBookNowTarget(rule, DateTimeOffset.UtcNow);
        if (targetResult.TargetDate is null)
        {
            return OperationResult.Failure(targetResult.FailureMessage!);
        }

        var targetDate = targetResult.TargetDate.Value;
        var settings = await repository.GetSettingsAsync(cancellationToken);
        var loaded = await TryLoadConfigurationAsync(settings.ExternalConfigurationPath, cancellationToken);
        if (loaded.Configuration is null)
        {
            return loaded.Failure!;
        }

        var currentPlayerIds = loaded.Configuration.Players
            .Select(static player => player.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (rule.PlayerIds.Any(playerId => !currentPlayerIds.Contains(playerId)))
        {
            return OperationResult.Failure(
                "A scheduled player is missing from the current external configuration; no worker was started.");
        }

        var attemptId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var attempt = new BookingAttempt
        {
            Id = attemptId,
            RuleId = rule.Id,
            TargetDate = targetDate,
            ScheduledForUtc = startedAt,
            StartedAtUtc = startedAt,
            Status = BookingAttemptStatus.Running,
            IsDryRun = mode != WorkerAutomationMode.Submit,
        };
        // Hold a unique UI lease only while checking submission risk and creating
        // the Running row. That row is the handoff barrier; release this lease
        // before launching the worker so the worker can acquire its own unique
        // process lease. Duplicate workers can no longer re-enter using an
        // identical attempt ID.
        await using (var lease = await repository.TryAcquireOccurrenceLockAsync(
                         targetDate,
                         $"{attemptId:D}:ui:{Environment.ProcessId}",
                         WorkerLeaseDuration,
                         cancellationToken))
        {
            if (lease is null)
            {
                return OperationResult.Failure("Another worker is already handling this target date.");
            }

            if (mode == WorkerAutomationMode.Submit &&
                await repository.HasSubmissionRiskAsync(targetDate, cancellationToken))
            {
                return OperationResult.Failure(
                    "A running, successful, or uncertain prior attempt blocks another submission for this target date.");
            }

            await repository.AddAttemptAsync(attempt, cancellationToken);
        }

        WorkerProcessResult processResult;
        try
        {
            processResult = await worker.ExecuteAsync(
                new WorkerRequest
                {
                    AttemptId = attemptId,
                    RuleId = rule.Id,
                    AccountConfigurationPath = settings.ExternalConfigurationPath!,
                    TargetDate = targetDate,
                    OrderedStartTimes = rule.StartTimes,
                    BookingKind = rule.BookingType == CoreBookingType.Singles
                        ? WorkerBookingKind.Singles
                        : WorkerBookingKind.Doubles,
                    DurationMinutes = rule.DurationMinutes,
                    PlayerIds = rule.PlayerIds,
                    CourtOrder = rule.CourtOrder,
                    Mode = mode,
                    TermsAuthorized = rule.TermsAuthorizedAtUtc is not null,
                    VisibleBrowser = true,
                    UserInitiated = true,
                    DiagnosticsDirectory = paths.DiagnosticsDirectory,
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var submissionWasPossible = mode == WorkerAutomationMode.Submit;
            await repository.UpdateAttemptAsync(
                attempt with
                {
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    Status = submissionWasPossible
                        ? BookingAttemptStatus.UncertainSubmission
                        : BookingAttemptStatus.Cancelled,
                    SanitizedMessage = submissionWasPossible
                        ? "The submit worker was interrupted. Inspect Matchpoint before taking any further action."
                        : "The user cancelled the diagnostic worker; no booking submission was authorized.",
                },
                CancellationToken.None);
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var submissionWasPossible = mode == WorkerAutomationMode.Submit;
            var message = submissionWasPossible
                ? "The submit worker stopped unexpectedly. Inspect Matchpoint before taking any further action."
                : "The diagnostic worker stopped unexpectedly; no booking submission was authorized.";
            await repository.UpdateAttemptAsync(
                attempt with
                {
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    Status = submissionWasPossible
                        ? BookingAttemptStatus.UncertainSubmission
                        : BookingAttemptStatus.Failed,
                    ErrorCode = "WorkerInvocationFailed",
                    SanitizedMessage = message,
                },
                CancellationToken.None);
            return OperationResult.Failure(message);
        }

        // The worker owns terminal-state classification because it knows whether
        // final submission may have begun. Never downgrade a terminal row it has
        // already persisted (especially UncertainSubmission) based on a coarser
        // process result returned to the UI.
        var workerPersistedAttempt = await repository.GetAttemptAsync(attemptId, cancellationToken);
        var completed = workerPersistedAttempt is not null &&
                        SubmissionRiskPolicy.IsTerminalWorkerStateAuthoritative(workerPersistedAttempt.Status)
            ? workerPersistedAttempt
            : MapCompletedAttempt(
                attempt,
                processResult,
                submissionPossible: mode == WorkerAutomationMode.Submit);
        if (workerPersistedAttempt is null ||
            !SubmissionRiskPolicy.IsTerminalWorkerStateAuthoritative(workerPersistedAttempt.Status))
        {
            await repository.UpdateAttemptAsync(completed, cancellationToken);
        }
        var expectedSuccess = mode == WorkerAutomationMode.Submit
            ? BookingAttemptStatus.Succeeded
            : BookingAttemptStatus.DryRunSucceeded;
        return completed.Status == expectedSuccess
            ? OperationResult.Success(completed.SanitizedMessage ?? SuccessFallback(mode))
            : OperationResult.Failure(completed.SanitizedMessage ?? "The booking worker stopped without confirming a booking.");
    }

    public Task<SchedulerHealthSnapshot> GetSchedulerHealthAsync(CancellationToken cancellationToken = default) =>
        scheduler.GetHealthAsync(cancellationToken);

    public async Task<IReadOnlyList<BookingAttemptSummary>> GetHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        var attempts = await repository.GetAttemptsAsync(1_000, cancellationToken);
        var rules = (await repository.GetRulesAsync(cancellationToken)).ToDictionary(static rule => rule.Id);
        return attempts.Select(attempt =>
        {
            rules.TryGetValue(attempt.RuleId, out var rule);
            return new BookingAttemptSummary(
                attempt.Id,
                attempt.RuleId,
                rule?.Name ?? "Deleted schedule",
                attempt.TargetDate,
                attempt.StartedAtUtc ?? attempt.ScheduledForUtc,
                attempt.CompletedAtUtc,
                ToOutcome(attempt.Status, attempt.ErrorCode),
                attempt.SelectedCourt,
                attempt.SelectedStartTime,
                rule?.DurationMinutes ?? 0,
                attempt.SanitizedMessage ?? attempt.Status.ToString(),
                attempt.ScreenshotPath,
                attempt.TracePath);
        }).ToArray();
    }

    public async Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        var removed = await repository.ClearAttemptsAsync(cancellationToken: cancellationToken);
        return OperationResult.Success(removed == 1
            ? "Cleared 1 non-protected local booking attempt. Safety records were retained; failure diagnostics expire automatically after 14 days."
            : $"Cleared {removed} non-protected local booking attempts. Safety records were retained; failure diagnostics expire automatically after 14 days.");
    }

    private ScheduleSummary ToSummary(BookingRule rule, DateTimeOffset nowUtc)
    {
        var nextTarget = scheduleService.GetNextUnopenedTargetDate(rule, nowUtc);
        DateTimeOffset? nextAttempt = nextTarget is { } target
            ? scheduleService.GetWindow(target).WorkerStartsAtUtc
            : null;
        var authorizationIsCurrent = rule.TermsAuthorizedAtUtc is { } authorizedAt &&
                                     authorizedAt <= nowUtc &&
                                     nowUtc - authorizedAt <= TermsAuthorizationLifetime;
        var status = !rule.Enabled
            ? "Paused"
            : !authorizationIsCurrent
                ? "Consent expired · edit and save"
                : nextTarget is not null
                    ? "Ready"
                    : rule.Kind == CoreRuleKind.OneTime &&
                      rule.TargetDate is { } oneTimeTarget &&
                      oneTimeTarget >= DateOnly.FromDateTime(
                          TimeZoneInfo.ConvertTime(nowUtc, scheduleService.TimeZone).DateTime)
                        ? "Open · use Book now"
                        : "No upcoming run";

        return new ScheduleSummary(
            rule.Id,
            rule.Name,
            rule.Kind == CoreRuleKind.OneTime ? UiRuleKind.OneTime : UiRuleKind.Weekly,
            rule.TargetDate,
            rule.Weekdays,
            rule.StartTimes,
            rule.BookingType == CoreBookingType.Singles ? UiBookingType.Singles : UiBookingType.Doubles,
            rule.DurationMinutes,
            rule.PlayerIds,
            rule.CourtOrder,
            rule.Enabled,
            rule.TermsAuthorizedAtUtc,
            nextAttempt,
            status);
    }

    private static BookingRuleDraft ToDraft(BookingRule rule) => new(
        rule.Id,
        rule.Name,
        rule.Kind == CoreRuleKind.OneTime ? UiRuleKind.OneTime : UiRuleKind.Weekly,
        rule.TargetDate,
        rule.Weekdays,
        rule.StartTimes,
        rule.BookingType == CoreBookingType.Singles ? UiBookingType.Singles : UiBookingType.Doubles,
        rule.DurationMinutes,
        rule.PlayerIds,
        rule.CourtOrder,
        rule.TermsAuthorizedAtUtc is not null,
        rule.TermsAuthorizedAtUtc);

    private static string? FindRuleConflict(BookingRuleDraft draft, IReadOnlyList<BookingRule> rules)
    {
        var conflict = rules.Any(rule =>
            rule.Id != draft.ExistingId &&
            ((draft.Kind == UiRuleKind.OneTime &&
              rule.Kind == CoreRuleKind.OneTime &&
              rule.TargetDate == draft.TargetDate) ||
             (draft.Kind == UiRuleKind.Weekly &&
              rule.Kind == CoreRuleKind.Weekly &&
              rule.Weekdays.Intersect(draft.Weekdays).Any())));
        return conflict
            ? "Another booking rule already targets this date or one of these recurring weekdays."
            : null;
    }

    private async Task<(ExternalConfiguration? Configuration, OperationResult? Failure)> TryLoadConfigurationAsync(
        string? path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return (null, OperationResult.Failure(
                "Validate an external account/player configuration on Setup before booking."));
        }

        try
        {
            var loaded = await configurationLoader.LoadAsync(path, cancellationToken);
            return (loaded.Configuration, null);
        }
        catch (ExternalConfigurationException exception)
        {
            return (null, OperationResult.Failure(
                $"The saved external configuration failed validation ({exception.Code}); no booking was attempted."));
        }
    }

    private async Task<OperationResult?> ValidateSavedConfigurationAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var loaded = await TryLoadConfigurationAsync(settings.ExternalConfigurationPath, cancellationToken);
        return loaded.Configuration is null ? loaded.Failure : null;
    }

    private async Task<OperationResult> SynchronizeOrDisableAsync(CancellationToken cancellationToken)
    {
        var settings = await repository.GetSettingsAsync(cancellationToken);
        if (!settings.UnattendedSubmissionEnabled)
        {
            return OperationResult.Success("Unattended submission is disabled.");
        }

        var result = await scheduler.SyncAsync(cancellationToken);
        if (!result.IsSuccess)
        {
            await DisableUnattendedSubmissionAsync(cancellationToken);
        }

        return result;
    }

    private async Task DisableUnattendedSubmissionAsync(CancellationToken cancellationToken)
    {
        var settings = await repository.GetSettingsAsync(cancellationToken);
        await repository.SaveSettingsAsync(
            settings with
            {
                UnattendedSubmissionEnabled = false,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            },
            cancellationToken);
        await scheduler.SyncAsync(cancellationToken);
    }

    private (DateOnly? TargetDate, string? FailureMessage) ResolveBookNowTarget(
        BookingRule rule,
        DateTimeOffset nowUtc)
    {
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, scheduleService.TimeZone);
        var today = DateOnly.FromDateTime(localNow.DateTime);
        if (rule.Kind == CoreRuleKind.OneTime)
        {
            if (rule.TargetDate is not { } target || target < today)
            {
                return (null, "This one-time booking date has passed.");
            }

            if (target > today.AddDays(3) || scheduleService.GetOpeningTimeUtc(target) > nowUtc)
            {
                return (null, "This booking is not open yet. Use Book now after its three-day booking window opens.");
            }

            return (target, null);
        }

        // Prefer the newly opened occurrence three days out. If this rule has
        // several weekdays already inside the window, the UI confirms this exact
        // resolved date before sending it back for a second validation.
        for (var offset = 3; offset >= 0; offset--)
        {
            var target = today.AddDays(offset);
            if (rule.Weekdays.Contains(target.DayOfWeek) && scheduleService.GetOpeningTimeUtc(target) <= nowUtc)
            {
                return (target, null);
            }
        }

        return (null, "No occurrence of this weekly rule is currently inside the three-day booking window.");
    }

    private (DateOnly? TargetDate, string? FailureMessage) ValidateRequestedBookNowTarget(
        BookingRule rule,
        DateOnly requestedTargetDate,
        DateTimeOffset nowUtc)
    {
        var resolved = ResolveBookNowTarget(rule, nowUtc);
        return resolved.TargetDate == requestedTargetDate
            ? resolved
            : (null, "The confirmed play date is no longer the currently opening occurrence. Review the schedule and try again.");
    }

    private static BookingAttempt MapCompletedAttempt(
        BookingAttempt attempt,
        WorkerProcessResult processResult,
        bool submissionPossible)
    {
        var workerResult = processResult.Result;
        if (workerResult is null || workerResult.AttemptId != attempt.Id)
        {
            var uncertain = SubmissionRiskPolicy.RequiresUncertainFallback(
                new SubmissionCompletionEvidence(
                    submissionPossible,
                    processResult.ProcessStarted,
                    ValidWorkerResult: false,
                    HasAutomationResult: false,
                    WorkerReportedAutomationFailure: false,
                    WorkerReportedCancellationOrUncertainty: false));
            return attempt with
            {
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Status = uncertain
                    ? BookingAttemptStatus.UncertainSubmission
                    : BookingAttemptStatus.Failed,
                ErrorCode = processResult.ProcessStarted ? "WorkerResultMissing" : "WorkerStartFailed",
                SanitizedMessage = processResult.ProcessStarted && !submissionPossible
                    ? "The diagnostic worker did not return a valid result. No final booking submission was authorized."
                    : processResult.FailureMessage,
            };
        }

        var automation = workerResult.AutomationResult;
        var fallbackIsUncertain = SubmissionRiskPolicy.RequiresUncertainFallback(
            new SubmissionCompletionEvidence(
                submissionPossible,
                processResult.ProcessStarted,
                ValidWorkerResult: true,
                HasAutomationResult: automation is not null,
                WorkerReportedAutomationFailure: workerResult.Outcome == WorkerOutcome.AutomationFailed,
                WorkerReportedCancellationOrUncertainty:
                    automation?.Status is WorkerAutomationStatus.Cancelled or WorkerAutomationStatus.SubmissionUncertain));
        return attempt with
        {
            CompletedAtUtc = workerResult.CompletedAtUtc == default
                ? DateTimeOffset.UtcNow
                : workerResult.CompletedAtUtc,
            Status = fallbackIsUncertain
                ? BookingAttemptStatus.UncertainSubmission
                : ToAttemptStatus(workerResult),
            SelectedCourt = automation?.CourtNumber,
            SelectedStartTime = automation?.StartTime,
            ErrorCode = automation?.Status.ToString() ?? workerResult.Outcome.ToString(),
            SanitizedMessage = fallbackIsUncertain
                ? "The submit worker did not conclusively prove that no booking was created. Inspect Matchpoint before taking any further action."
                : workerResult.Message,
            ScreenshotPath = automation?.ScreenshotPath,
            TracePath = automation?.TracePath,
        };
    }

    private static BookingAttemptStatus ToAttemptStatus(WorkerResult result)
    {
        if (result.Outcome == WorkerOutcome.MissedWindow)
        {
            return BookingAttemptStatus.MissedWindow;
        }

        if (result.Outcome is WorkerOutcome.InvalidRequest or WorkerOutcome.ConfigurationFailed)
        {
            return BookingAttemptStatus.ValidationFailed;
        }

        if (result.Outcome == WorkerOutcome.AlreadyRunning)
        {
            return BookingAttemptStatus.Failed;
        }

        return result.AutomationResult?.Status switch
        {
            WorkerAutomationStatus.Succeeded => BookingAttemptStatus.Succeeded,
            WorkerAutomationStatus.ReadOnlyComplete => BookingAttemptStatus.DryRunSucceeded,
            WorkerAutomationStatus.DryRunComplete => BookingAttemptStatus.DryRunSucceeded,
            WorkerAutomationStatus.NoAvailability => BookingAttemptStatus.Unavailable,
            WorkerAutomationStatus.ValidationFailed => BookingAttemptStatus.ValidationFailed,
            WorkerAutomationStatus.AuthenticationFailed => BookingAttemptStatus.AuthenticationFailed,
            WorkerAutomationStatus.PlayerMismatch => BookingAttemptStatus.PlayerNotFound,
            WorkerAutomationStatus.NonZeroPrice => BookingAttemptStatus.NonZeroPrice,
            WorkerAutomationStatus.PageStructureChanged => BookingAttemptStatus.SiteChanged,
            WorkerAutomationStatus.CaptchaOrMfaRequired => BookingAttemptStatus.CaptchaOrMfaRequired,
            WorkerAutomationStatus.SubmissionUncertain => BookingAttemptStatus.UncertainSubmission,
            WorkerAutomationStatus.Cancelled => BookingAttemptStatus.Cancelled,
            _ => BookingAttemptStatus.Failed,
        };
    }

    private static AttemptOutcome ToOutcome(BookingAttemptStatus status, string? errorCode) => status switch
    {
        BookingAttemptStatus.Pending => AttemptOutcome.Pending,
        BookingAttemptStatus.Running => AttemptOutcome.Preparing,
        BookingAttemptStatus.Succeeded => AttemptOutcome.Successful,
        BookingAttemptStatus.DryRunSucceeded when string.Equals(
            errorCode,
            nameof(WorkerAutomationStatus.ReadOnlyComplete),
            StringComparison.Ordinal) => AttemptOutcome.GridInspected,
        BookingAttemptStatus.DryRunSucceeded => AttemptOutcome.DryRunValidated,
        BookingAttemptStatus.Unavailable => AttemptOutcome.Unavailable,
        BookingAttemptStatus.MissedWindow => AttemptOutcome.MissedWindow,
        BookingAttemptStatus.ValidationFailed or
            BookingAttemptStatus.AuthenticationFailed or
            BookingAttemptStatus.PlayerNotFound or
            BookingAttemptStatus.NonZeroPrice or
            BookingAttemptStatus.SiteChanged or
            BookingAttemptStatus.CaptchaOrMfaRequired or
            BookingAttemptStatus.UncertainSubmission => AttemptOutcome.NeedsAttention,
        _ => AttemptOutcome.Failed,
    };

    private static string SuccessFallback(WorkerAutomationMode mode) => mode switch
    {
        WorkerAutomationMode.ReadOnly => "Grid inspection completed without opening a checkout or submitting a booking.",
        WorkerAutomationMode.DryRun => "Dry run completed through player and $0.00 validation without submitting a booking.",
        _ => "The court was booked successfully.",
    };
}
