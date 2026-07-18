namespace RockcliffeCourtBooker.App.Models;

public enum BookingType
{
    Singles,
    Doubles
}

public enum RuleKind
{
    OneTime,
    Weekly
}

public enum UiStatusTone
{
    Neutral,
    Info,
    Success,
    Warning,
    Error
}

public enum AttemptOutcome
{
    Pending,
    Preparing,
    Successful,
    GridInspected,
    DryRunValidated,
    Unavailable,
    Failed,
    MissedWindow,
    NeedsAttention
}

public sealed record PlayerSummary(string Id, string DisplayName);

public sealed record ConfigurationSummary(
    string FilePath,
    string MemberName,
    string Username,
    IReadOnlyList<PlayerSummary> Players,
    string SecurityWarning);

public sealed record ConfigurationValidationResult(
    bool IsValid,
    string Message,
    ConfigurationSummary? Configuration = null)
{
    public static ConfigurationValidationResult Invalid(string message) => new(false, message);

    public static ConfigurationValidationResult Valid(ConfigurationSummary configuration, string message) =>
        new(true, message, configuration);
}

public sealed record OperationResult(bool IsSuccess, string Message)
{
    public static OperationResult Success(string message) => new(true, message);

    public static OperationResult Failure(string message) => new(false, message);
}

public sealed record BookingRuleDraft(
    Guid? ExistingId,
    string Name,
    RuleKind Kind,
    DateOnly? TargetDate,
    IReadOnlyList<DayOfWeek> Weekdays,
    IReadOnlyList<TimeOnly> StartTimes,
    BookingType BookingType,
    int DurationMinutes,
    IReadOnlyList<string> PlayerIds,
    IReadOnlyList<int> CourtOrder,
    bool TermsAuthorized,
    DateTimeOffset? TermsAuthorizedAt);

public sealed record ScheduleSummary(
    Guid Id,
    string Name,
    RuleKind Kind,
    DateOnly? TargetDate,
    IReadOnlyList<DayOfWeek> Weekdays,
    IReadOnlyList<TimeOnly> StartTimes,
    BookingType BookingType,
    int DurationMinutes,
    IReadOnlyList<string> PlayerIds,
    IReadOnlyList<int> CourtOrder,
    bool IsEnabled,
    DateTimeOffset? TermsAuthorizedAt,
    DateTimeOffset? NextAttempt,
    string Status);

public sealed record BookingAttemptSummary(
    Guid Id,
    Guid? RuleId,
    string RuleName,
    DateOnly TargetDate,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    AttemptOutcome Outcome,
    int? CourtNumber,
    TimeOnly? StartTime,
    int DurationMinutes,
    string Message,
    string? ScreenshotPath,
    string? TracePath);

public sealed record SchedulerHealthSnapshot(
    bool IsHealthy,
    string Status,
    DateTimeOffset? NextRun,
    string Detail);

public sealed record ManualBookingTarget(
    bool IsAvailable,
    DateOnly? TargetDate,
    string Message);
