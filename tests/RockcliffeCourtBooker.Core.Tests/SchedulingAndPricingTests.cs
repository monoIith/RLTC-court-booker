using System.Globalization;

namespace RockcliffeCourtBooker.Core.Tests;

public sealed class SchedulingAndPricingTests
{
    private readonly BookingScheduleService _schedule = new(TorontoTimeZone.GetSystemTimeZone());

    [Theory]
    [InlineData(2026, 7, 20, "2026-07-17T12:00:00+00:00")]
    [InlineData(2027, 1, 1, "2026-12-29T13:00:00+00:00")]
    [InlineData(2028, 3, 1, "2028-02-27T13:00:00+00:00")]
    [InlineData(2026, 3, 11, "2026-03-08T12:00:00+00:00")]
    [InlineData(2026, 11, 4, "2026-11-01T13:00:00+00:00")]
    public void GetWindow_UsesThreeCalendarDaysAndTorontoDst(
        int year,
        int month,
        int day,
        string expectedOpeningUtc)
    {
        var window = _schedule.GetWindow(new DateOnly(year, month, day));

        Assert.Equal(DateTimeOffset.Parse(expectedOpeningUtc, CultureInfo.InvariantCulture), window.OpensAtUtc);
        Assert.Equal(window.OpensAtUtc.AddMinutes(-2), window.WorkerStartsAtUtc);
        Assert.Equal(window.OpensAtUtc.AddMinutes(15), window.UnattendedCutoffAtUtc);
    }

    [Fact]
    public void GetNextUnopenedTargetDate_AdvancesAfterExactOpening()
    {
        var rule = TestModels.ValidWeeklyRule(DayOfWeek.Monday);
        var justBefore = new DateTimeOffset(2026, 7, 17, 11, 59, 59, TimeSpan.Zero);
        var atOpening = new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(new DateOnly(2026, 7, 20), _schedule.GetNextUnopenedTargetDate(rule, justBefore));
        Assert.Equal(new DateOnly(2026, 7, 27), _schedule.GetNextUnopenedTargetDate(rule, atOpening));
    }

    [Fact]
    public void UnattendedWindow_IsHalfOpenAtTheCutoff()
    {
        var targetDate = new DateOnly(2026, 7, 20);
        var cutoff = _schedule.GetUnattendedCutoffUtc(targetDate);

        Assert.True(_schedule.IsWithinUnattendedWindow(targetDate, cutoff.AddTicks(-1)));
        Assert.False(_schedule.IsMissedWindow(targetDate, cutoff.AddTicks(-1)));
        Assert.False(_schedule.IsWithinUnattendedWindow(targetDate, cutoff));
        Assert.True(_schedule.IsMissedWindow(targetDate, cutoff));
    }

    [Fact]
    public void CandidateOrdering_IsTimeFirstThenCourtPriority()
    {
        var candidates = new CandidateOrderingService().CreateCandidates(
            [new TimeOnly(18, 0), new TimeOnly(19, 30)],
            [2, 1, 4, 3]);

        Assert.Equal(
            [
                new BookingCandidate(new TimeOnly(18, 0), 2),
                new BookingCandidate(new TimeOnly(18, 0), 1),
                new BookingCandidate(new TimeOnly(18, 0), 4),
                new BookingCandidate(new TimeOnly(18, 0), 3),
                new BookingCandidate(new TimeOnly(19, 30), 2),
                new BookingCandidate(new TimeOnly(19, 30), 1),
                new BookingCandidate(new TimeOnly(19, 30), 4),
                new BookingCandidate(new TimeOnly(19, 30), 3),
            ],
            candidates);
    }

    [Theory]
    [InlineData("$0.00", true)]
    [InlineData("Price for the full court:  $ 0.00", true)]
    [InlineData("CAD\u00a0$0.00", true)]
    [InlineData("$62.73", false)]
    [InlineData("-$0.00", false)]
    [InlineData("$-0.00", false)]
    [InlineData("$0", false)]
    [InlineData("$0.000", false)]
    [InlineData("$0.00 and $0.00", false)]
    [InlineData("free", false)]
    [InlineData(null, false)]
    public void PriceParser_FailsClosedUnlessExactlyOneValidZeroAmount(string? text, bool expected)
    {
        Assert.Equal(expected, new PriceParser().IsExactlyZero(text));
    }

    [Fact]
    public void SensitiveDataRedactor_RedactsLongestSecretsAndHandlesNull()
    {
        var result = SensitiveDataRedactor.Redact(
            "password=secret-long; short=secret",
            ["secret", "secret-long"]);

        Assert.Equal("password=[REDACTED]; short=[REDACTED]", result);
        Assert.Null(SensitiveDataRedactor.Redact(null, ["secret"]));
    }

    [Theory]
    [InlineData(true, "----AppNotificationActivated:")]
    [InlineData(false)]
    [InlineData(false, "----AppNotificationActivated:", "unexpected")]
    [InlineData(false, "--history")]
    [InlineData(false, "----appnotificationactivated:")]
    public void NotificationActivationLaunch_RecognizesOnlyTheWindowsSentinel(
        bool expected,
        params string[] arguments)
    {
        Assert.Equal(expected, NotificationActivationLaunch.IsActivationLaunch(arguments));
    }
}
