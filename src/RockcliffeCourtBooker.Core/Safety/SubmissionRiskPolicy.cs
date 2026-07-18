namespace RockcliffeCourtBooker.Core;

public sealed record SubmissionCompletionEvidence(
    bool SubmissionAuthorized,
    bool ProcessStarted,
    bool ValidWorkerResult,
    bool HasAutomationResult,
    bool WorkerReportedAutomationFailure,
    bool WorkerReportedCancellationOrUncertainty);

public static class SubmissionRiskPolicy
{
    public static bool IsTerminalWorkerStateAuthoritative(BookingAttemptStatus status) =>
        status is not BookingAttemptStatus.Pending and not BookingAttemptStatus.Running;

    public static bool RequiresUncertainFallback(SubmissionCompletionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!evidence.SubmissionAuthorized || !evidence.ProcessStarted)
        {
            return false;
        }

        if (!evidence.ValidWorkerResult)
        {
            return true;
        }

        return evidence.WorkerReportedCancellationOrUncertainty ||
               (!evidence.HasAutomationResult && evidence.WorkerReportedAutomationFailure);
    }
}
