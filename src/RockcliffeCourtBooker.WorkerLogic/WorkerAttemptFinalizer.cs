using RockcliffeCourtBooker.Automation.Contracts;
using RockcliffeCourtBooker.Core;
using RockcliffeCourtBooker.Worker.Contracts;

namespace RockcliffeCourtBooker.WorkerLogic;

public static class WorkerAttemptFinalizer
{
    public static async Task<BookingWorkerResult> RunAndFinalizeAttemptAsync(
        IBookingRepository repository,
        BookingAttempt attempt,
        Func<CancellationToken, Task<BookingWorkerResult>> operation,
        CancellationToken cancellationToken)
    {
        BookingAttemptStatus? statusOverride = attempt.IsDryRun
            ? BookingAttemptStatus.Failed
            : BookingAttemptStatus.UncertainSubmission;
        var result = new BookingWorkerResult
        {
            AttemptId = attempt.Id,
            Outcome = WorkerOutcome.AutomationFailed,
            Message = attempt.IsDryRun
                ? "The diagnostic worker stopped safely before it completed."
                : "The submission outcome could not be proven. Inspect Matchpoint before taking any further action.",
        };
        try
        {
            result = await operation(cancellationToken);
            // The automation layer distinguishes cancellation immediately before
            // the sole click from cancellation after submission has begun.
            statusOverride = null;
        }
        catch (OperationCanceledException)
        {
            statusOverride = attempt.IsDryRun
                ? BookingAttemptStatus.Cancelled
                : BookingAttemptStatus.UncertainSubmission;
            result = new BookingWorkerResult
            {
                AttemptId = attempt.Id,
                Outcome = WorkerOutcome.AutomationFailed,
                Message = attempt.IsDryRun
                    ? "The diagnostic worker was cancelled or exceeded its active-work limit."
                    : "The submit worker was cancelled and its outcome could not be proven. Inspect Matchpoint before taking any further action.",
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            statusOverride = attempt.IsDryRun
                ? BookingAttemptStatus.Failed
                : BookingAttemptStatus.UncertainSubmission;
            result = new BookingWorkerResult
            {
                AttemptId = attempt.Id,
                Outcome = WorkerOutcome.AutomationFailed,
                Message = attempt.IsDryRun
                    ? "The diagnostic worker stopped safely before it completed."
                    : "The submission outcome could not be proven. Inspect Matchpoint before taking any further action.",
            };
        }
        finally
        {
            // Terminal persistence ignores caller cancellation because it is part
            // of the duplicate-submission safety boundary.
            await FinalizeAttemptAsync(repository, attempt, result, statusOverride);
        }

        return result;
    }

    public static BookingAttemptStatus MapAttemptStatus(BookingWorkerResult result)
    {
        if (result.Outcome == WorkerOutcome.MissedWindow)
        {
            return BookingAttemptStatus.MissedWindow;
        }

        if (result.Outcome is WorkerOutcome.InvalidRequest or WorkerOutcome.ConfigurationFailed)
        {
            return BookingAttemptStatus.ValidationFailed;
        }

        return result.AutomationResult?.Status switch
        {
            BookingAutomationStatus.Succeeded => BookingAttemptStatus.Succeeded,
            BookingAutomationStatus.ReadOnlyComplete => BookingAttemptStatus.DryRunSucceeded,
            BookingAutomationStatus.DryRunComplete => BookingAttemptStatus.DryRunSucceeded,
            BookingAutomationStatus.NoAvailability => BookingAttemptStatus.Unavailable,
            BookingAutomationStatus.ValidationFailed => BookingAttemptStatus.ValidationFailed,
            BookingAutomationStatus.AuthenticationFailed => BookingAttemptStatus.AuthenticationFailed,
            BookingAutomationStatus.PlayerMismatch => BookingAttemptStatus.PlayerNotFound,
            BookingAutomationStatus.NonZeroPrice => BookingAttemptStatus.NonZeroPrice,
            BookingAutomationStatus.PageStructureChanged => BookingAttemptStatus.SiteChanged,
            BookingAutomationStatus.CaptchaOrMfaRequired => BookingAttemptStatus.CaptchaOrMfaRequired,
            BookingAutomationStatus.SubmissionUncertain => BookingAttemptStatus.UncertainSubmission,
            BookingAutomationStatus.Cancelled => BookingAttemptStatus.Cancelled,
            _ => BookingAttemptStatus.Failed,
        };
    }

    public static bool IsPreSubmissionCutoffCancellation(
        bool enforceUnattendedCutoff,
        bool cutoffTimerFired,
        DateTimeOffset nowUtc,
        DateTimeOffset cutoffUtc,
        BookingAutomationStatus status) =>
        enforceUnattendedCutoff &&
        status == BookingAutomationStatus.Cancelled &&
        (cutoffTimerFired || nowUtc >= cutoffUtc);

    private static Task FinalizeAttemptAsync(
        IBookingRepository repository,
        BookingAttempt attempt,
        BookingWorkerResult result,
        BookingAttemptStatus? statusOverride)
    {
        var automation = result.AutomationResult;
        var completed = attempt with
        {
            CompletedAtUtc = result.CompletedAtUtc == default
                ? DateTimeOffset.UtcNow
                : result.CompletedAtUtc,
            Status = statusOverride ?? MapAttemptStatus(result),
            SelectedCourt = automation?.CourtNumber,
            SelectedStartTime = automation?.StartTime,
            ErrorCode = automation?.Status.ToString() ?? result.Outcome.ToString(),
            SanitizedMessage = result.Message,
            ScreenshotPath = automation?.ScreenshotPath,
            TracePath = automation?.TracePath,
        };
        return repository.UpdateAttemptAsync(completed, CancellationToken.None);
    }
}
