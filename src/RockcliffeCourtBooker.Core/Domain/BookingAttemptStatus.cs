namespace RockcliffeCourtBooker.Core;

public enum BookingAttemptStatus
{
    Pending = 1,
    Running = 2,
    Succeeded = 3,
    DryRunSucceeded = 4,
    Unavailable = 5,
    MissedWindow = 6,
    ValidationFailed = 7,
    AuthenticationFailed = 8,
    PlayerNotFound = 9,
    NonZeroPrice = 10,
    SiteChanged = 11,
    CaptchaOrMfaRequired = 12,
    UncertainSubmission = 13,
    Cancelled = 14,
    Failed = 15,
}
