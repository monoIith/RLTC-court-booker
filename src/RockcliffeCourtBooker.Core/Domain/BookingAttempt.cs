namespace RockcliffeCourtBooker.Core;

public sealed record BookingAttempt
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid RuleId { get; init; }

    public DateOnly TargetDate { get; init; }

    public DateTimeOffset ScheduledForUtc { get; init; }

    public DateTimeOffset? StartedAtUtc { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }

    public BookingAttemptStatus Status { get; init; } = BookingAttemptStatus.Pending;

    public int? SelectedCourt { get; init; }

    public TimeOnly? SelectedStartTime { get; init; }

    public string? ErrorCode { get; init; }

    public string? SanitizedMessage { get; init; }

    public string? ScreenshotPath { get; init; }

    public string? TracePath { get; init; }

    public bool IsDryRun { get; init; }
}
