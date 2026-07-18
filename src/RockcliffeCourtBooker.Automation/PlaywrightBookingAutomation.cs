using Microsoft.Playwright;
using RockcliffeCourtBooker.Automation.Contracts;
using RockcliffeCourtBooker.Automation.Diagnostics;
using RockcliffeCourtBooker.Automation.Matchpoint;
using RockcliffeCourtBooker.Automation.Planning;
using RockcliffeCourtBooker.Automation.Security;
using RockcliffeCourtBooker.Automation.Validation;

namespace RockcliffeCourtBooker.Automation;

public sealed class PlaywrightBookingAutomation : IBookingAutomation
{
    private static readonly TimeSpan DiagnosticRetention = TimeSpan.FromDays(14);

    public async Task<BookingAutomationResult> ExecuteAsync(
        MatchpointBookingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validationErrors = BookingRequestValidator.Validate(request);
        if (validationErrors.Count > 0)
        {
            return Result(
                request,
                BookingAutomationStatus.ValidationFailed,
                string.Join(" ", validationErrors));
        }

        var redactor = new SensitiveDataRedactor(
            request.Credentials.Password,
            request.Credentials.Username);
        AutomationDiagnostics.DeleteExpiredArtifacts(request.DiagnosticsDirectory, DiagnosticRetention);

        if (!PlaywrightRuntime.ConfigureBundledBrowserPath(request.ChromiumExecutablePath))
        {
            return Result(
                request,
                BookingAutomationStatus.UnexpectedError,
                "The bundled Playwright Chromium installation is missing; no browser was opened.");
        }

        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        IBrowser? browser = null;
        IBrowserContext? context = null;
        IPage? page = null;
        MatchpointPage? matchpoint = null;
        AutomationDiagnostics? diagnostics = null;
        var finalSubmissionStarted = false;

        try
        {
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = request.Headless,
                ExecutablePath = request.ChromiumExecutablePath,
            });
            context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                Locale = "en-CA",
                TimezoneId = "America/Toronto",
            });
            page = await context.NewPageAsync();
            page.SetDefaultTimeout(10_000);
            page.SetDefaultNavigationTimeout(20_000);
            matchpoint = new MatchpointPage(page);
            diagnostics = new AutomationDiagnostics(request.DiagnosticsDirectory, request.AttemptId);

            // Tracing starts only after authentication so the password cannot be captured in a trace.
            await matchpoint.LogInAsync(request.Credentials, cancellationToken);
            await diagnostics.StartTraceAsync(context);
            await matchpoint.OpenGridAsync(request.BookingKind, request.TargetDate);
            await WaitForOpeningAndRefreshAsync(request, matchpoint, cancellationToken);

            var candidates = CandidatePlanner.Build(request.OrderedStartTimes, request.CourtOrder);
            if (request.Mode == AutomationMode.ReadOnly)
            {
                var available = new List<BookingCandidate>();
                foreach (var candidate in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (await matchpoint.IsGridCandidateOpenAsync(candidate))
                    {
                        available.Add(candidate);
                    }
                }

                await diagnostics.CompleteAsync(context, page, retain: false, matchpoint.ClearSensitiveInputsAsync);
                return new BookingAutomationResult
                {
                    AttemptId = request.AttemptId,
                    Status = BookingAutomationStatus.ReadOnlyComplete,
                    Message = available.Count == 0
                        ? "Grid inspection completed; no requested grid cells were open."
                        : "Grid inspection completed. Open cells are reported without opening a booking popup.",
                    AvailableCandidates = available,
                };
            }

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await matchpoint.OpenDurationAsync(
                        candidate,
                        request.BookingKind,
                        request.DurationMinutes);
                    await matchpoint.SelectPlayersAndValidateCheckoutAsync(request, candidate);

                    if (request.TermsAuthorized)
                    {
                        await matchpoint.AcceptTermsAsync();
                    }

                    if (request.Mode == AutomationMode.DryRun)
                    {
                        await diagnostics.CompleteAsync(
                            context,
                            page,
                            retain: false,
                            matchpoint.ClearSensitiveInputsAsync);
                        return new BookingAutomationResult
                        {
                            AttemptId = request.AttemptId,
                            Status = BookingAutomationStatus.DryRunComplete,
                            Message = "Dry run reached a validated $0.00 checkout and stopped before Book.",
                            CourtNumber = candidate.CourtNumber,
                            StartTime = candidate.StartTime,
                        };
                    }

                    var outcome = await matchpoint.SubmitOnceAsync(
                        request,
                        candidate,
                        cancellationToken,
                        () => finalSubmissionStarted = true);
                    if (outcome == SubmissionOutcome.Confirmed)
                    {
                        await diagnostics.CompleteAsync(
                            context,
                            page,
                            retain: false,
                            matchpoint.ClearSensitiveInputsAsync);
                        return new BookingAutomationResult
                        {
                            AttemptId = request.AttemptId,
                            Status = BookingAutomationStatus.Succeeded,
                            Message = "Matchpoint confirmed the booking.",
                            CourtNumber = candidate.CourtNumber,
                            StartTime = candidate.StartTime,
                        };
                    }

                    if (outcome == SubmissionOutcome.Uncertain)
                    {
                        throw new MatchpointAutomationException(
                            BookingAutomationStatus.SubmissionUncertain,
                            "Book was clicked once, but Matchpoint did not provide conclusive success or failure. No retry was made.");
                    }

                    // Matchpoint conclusively said no booking was created. It is safe to try the next configured candidate.
                    finalSubmissionStarted = false;
                    await matchpoint.NavigateBackToGridAsync(request.BookingKind, request.TargetDate);
                }
                catch (CandidateUnavailableException)
                {
                    await matchpoint.NavigateBackToGridAsync(request.BookingKind, request.TargetDate);
                }
            }

            await diagnostics.CompleteAsync(context, page, retain: false, matchpoint.ClearSensitiveInputsAsync);
            return Result(
                request,
                BookingAutomationStatus.NoAvailability,
                "None of the requested time and clay-court combinations could accommodate the full duration.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var artifacts = await CompleteDiagnosticsSafelyAsync(diagnostics, context, page, matchpoint, retain: true);
            var status = finalSubmissionStarted
                ? BookingAutomationStatus.SubmissionUncertain
                : BookingAutomationStatus.Cancelled;
            var message = finalSubmissionStarted
                ? "The booking attempt was cancelled after final submission began. The result is uncertain and no retry was made."
                : "The booking attempt was cancelled before final submission began.";
            return Result(
                request,
                status,
                message,
                artifacts);
        }
        catch (MatchpointAutomationException exception)
        {
            var artifacts = await CompleteDiagnosticsSafelyAsync(diagnostics, context, page, matchpoint, retain: true);
            return Result(request, exception.Status, redactor.Redact(exception.Message), artifacts);
        }
        catch (PlaywrightException exception)
        {
            var artifacts = await CompleteDiagnosticsSafelyAsync(diagnostics, context, page, matchpoint, retain: true);
            var status = finalSubmissionStarted
                ? BookingAutomationStatus.SubmissionUncertain
                : BookingAutomationStatus.PageStructureChanged;
            var message = finalSubmissionStarted
                ? "Browser automation failed after final submission began. The result is uncertain and no retry was made."
                : $"Matchpoint automation could not safely continue: {redactor.Redact(exception.Message)}";
            return Result(request, status, message, artifacts);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var artifacts = await CompleteDiagnosticsSafelyAsync(diagnostics, context, page, matchpoint, retain: true);
            var status = finalSubmissionStarted
                ? BookingAutomationStatus.SubmissionUncertain
                : BookingAutomationStatus.UnexpectedError;
            var message = finalSubmissionStarted
                ? "An unexpected error occurred after final submission began. The result is uncertain and no retry was made."
                : $"The worker stopped safely after an unexpected error: {redactor.Redact(exception.Message)}";
            return Result(request, status, message, artifacts);
        }
        finally
        {
            if (context is not null)
            {
                await context.CloseAsync();
            }

            if (browser is not null)
            {
                await browser.CloseAsync();
            }
        }
    }

    private static async Task WaitForOpeningAndRefreshAsync(
        MatchpointBookingRequest request,
        MatchpointPage matchpoint,
        CancellationToken cancellationToken)
    {
        if (request.BookingOpensAtUtc is not { } opensAt)
        {
            return;
        }

        var delay = opensAt - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }

        await matchpoint.RefreshGridAsync(request.BookingKind, request.TargetDate);
    }

    private static async Task<DiagnosticArtifacts?> CompleteDiagnosticsSafelyAsync(
        AutomationDiagnostics? diagnostics,
        IBrowserContext? context,
        IPage? page,
        MatchpointPage? matchpoint,
        bool retain)
    {
        if (diagnostics is null || context is null || page is null || matchpoint is null)
        {
            return null;
        }

        try
        {
            return await diagnostics.CompleteAsync(
                context,
                page,
                retain,
                matchpoint.ClearSensitiveInputsAsync);
        }
        catch (PlaywrightException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static BookingAutomationResult Result(
        MatchpointBookingRequest request,
        BookingAutomationStatus status,
        string message,
        DiagnosticArtifacts? artifacts = null)
    {
        return new BookingAutomationResult
        {
            AttemptId = request.AttemptId,
            Status = status,
            Message = message,
            ScreenshotPath = artifacts?.ScreenshotPath,
            TracePath = artifacts?.TracePath,
        };
    }
}
