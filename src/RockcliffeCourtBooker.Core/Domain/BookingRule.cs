namespace RockcliffeCourtBooker.Core;

public sealed record BookingRule
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = string.Empty;

    public BookingRuleKind Kind { get; init; }

    public DateOnly? TargetDate { get; init; }

    public IReadOnlyList<DayOfWeek> Weekdays { get; init; } = [];

    public IReadOnlyList<TimeOnly> StartTimes { get; init; } = [];

    public BookingType BookingType { get; init; }

    public int DurationMinutes { get; init; }

    public IReadOnlyList<string> PlayerIds { get; init; } = [];

    public IReadOnlyList<int> CourtOrder { get; init; } = [1, 2, 3, 4];

    public bool Enabled { get; init; } = true;

    public DateTimeOffset? TermsAuthorizedAtUtc { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
