namespace RockcliffeCourtBooker.Automation.Contracts;

public enum BookingAutomationStatus
{
    Succeeded,
    ReadOnlyComplete,
    DryRunComplete,
    NoAvailability,
    ValidationFailed,
    AuthenticationFailed,
    PlayerMismatch,
    NonZeroPrice,
    CaptchaOrMfaRequired,
    PageStructureChanged,
    BookingRejected,
    SubmissionUncertain,
    Cancelled,
    UnexpectedError,
}
