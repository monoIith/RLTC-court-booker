using RockcliffeCourtBooker.Core;

namespace RockcliffeCourtBooker.Core.Tests;

public sealed class SubmissionRiskPolicyTests
{
    [Theory]
    [InlineData(BookingAttemptStatus.Succeeded)]
    [InlineData(BookingAttemptStatus.UncertainSubmission)]
    [InlineData(BookingAttemptStatus.ValidationFailed)]
    public void TerminalWorkerStateIsAuthoritative(BookingAttemptStatus status)
    {
        Assert.True(SubmissionRiskPolicy.IsTerminalWorkerStateAuthoritative(status));
    }

    [Theory]
    [InlineData(BookingAttemptStatus.Pending)]
    [InlineData(BookingAttemptStatus.Running)]
    public void IncompleteWorkerStateNeedsReconciliation(BookingAttemptStatus status)
    {
        Assert.False(SubmissionRiskPolicy.IsTerminalWorkerStateAuthoritative(status));
    }

    [Fact]
    public void MissingResultAfterSubmitWorkerStartsIsUncertain()
    {
        var evidence = new SubmissionCompletionEvidence(
            SubmissionAuthorized: true,
            ProcessStarted: true,
            ValidWorkerResult: false,
            HasAutomationResult: false,
            WorkerReportedAutomationFailure: false,
            WorkerReportedCancellationOrUncertainty: false);

        Assert.True(SubmissionRiskPolicy.RequiresUncertainFallback(evidence));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    public void CancelledOrDetailFreeFailedSubmitWorkerIsUncertain(
        bool hasAutomationResult,
        bool workerAutomationFailed,
        bool cancellationOrUncertainty)
    {
        var evidence = new SubmissionCompletionEvidence(
            SubmissionAuthorized: true,
            ProcessStarted: true,
            ValidWorkerResult: true,
            HasAutomationResult: hasAutomationResult,
            WorkerReportedAutomationFailure: workerAutomationFailed,
            WorkerReportedCancellationOrUncertainty: cancellationOrUncertainty);

        Assert.True(SubmissionRiskPolicy.RequiresUncertainFallback(evidence));
    }

    [Fact]
    public void DiagnosticWorkerNeverCreatesSubmissionUncertainty()
    {
        var evidence = new SubmissionCompletionEvidence(
            SubmissionAuthorized: false,
            ProcessStarted: true,
            ValidWorkerResult: false,
            HasAutomationResult: false,
            WorkerReportedAutomationFailure: true,
            WorkerReportedCancellationOrUncertainty: true);

        Assert.False(SubmissionRiskPolicy.RequiresUncertainFallback(evidence));
    }
}
