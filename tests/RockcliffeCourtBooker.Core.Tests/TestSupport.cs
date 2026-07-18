using RockcliffeCourtBooker.Core;

namespace RockcliffeCourtBooker.Core.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rockcliffe-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string GetPath(string fileName) => System.IO.Path.Combine(Path, fileName);

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

internal sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => UtcNow;
}

internal static class TestModels
{
    public static BookingRule ValidOneTimeRule(
        DateOnly? targetDate = null,
        BookingType type = BookingType.Singles,
        bool enabled = true) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Monday evening",
        Kind = BookingRuleKind.OneTime,
        TargetDate = targetDate ?? new DateOnly(2026, 7, 20),
        StartTimes = [new TimeOnly(18, 0), new TimeOnly(18, 30)],
        BookingType = type,
        DurationMinutes = 90,
        PlayerIds = type == BookingType.Singles ? ["player-one"] : ["one", "two", "three"],
        CourtOrder = [2, 1, 4, 3],
        Enabled = enabled,
        TermsAuthorizedAtUtc = enabled ? new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero) : null,
        CreatedAtUtc = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero),
        UpdatedAtUtc = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero),
    };

    public static BookingRule ValidWeeklyRule(params DayOfWeek[] weekdays) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Weekly tennis",
        Kind = BookingRuleKind.Weekly,
        Weekdays = weekdays.Length == 0 ? [DayOfWeek.Monday] : weekdays,
        StartTimes = [new TimeOnly(19, 30)],
        BookingType = BookingType.Doubles,
        DurationMinutes = 120,
        PlayerIds = ["one", "two", "three"],
        CourtOrder = [1, 2, 3, 4],
        Enabled = true,
        TermsAuthorizedAtUtc = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero),
        CreatedAtUtc = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero),
        UpdatedAtUtc = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero),
    };
}
