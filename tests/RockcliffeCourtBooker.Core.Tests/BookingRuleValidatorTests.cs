namespace RockcliffeCourtBooker.Core.Tests;

public sealed class BookingRuleValidatorTests
{
    private readonly BookingRuleValidator _validator = new();

    [Fact]
    public void Validate_AcceptsValidSinglesAndDoublesRules()
    {
        Assert.True(_validator.Validate(TestModels.ValidOneTimeRule(type: BookingType.Singles)).IsValid);
        Assert.True(_validator.Validate(TestModels.ValidOneTimeRule(type: BookingType.Doubles)).IsValid);
        Assert.True(_validator.Validate(TestModels.ValidWeeklyRule(DayOfWeek.Monday, DayOfWeek.Friday)).IsValid);
    }

    [Theory]
    [InlineData(BookingType.Singles, 0)]
    [InlineData(BookingType.Singles, 2)]
    [InlineData(BookingType.Doubles, 1)]
    [InlineData(BookingType.Doubles, 4)]
    public void Validate_RequiresExactPartnerCount(BookingType type, int count)
    {
        var rule = TestModels.ValidOneTimeRule(type: type) with
        {
            PlayerIds = Enumerable.Range(1, count).Select(static index => $"player-{index}").ToArray(),
        };

        var result = _validator.Validate(rule);

        Assert.Contains(result.Issues, static issue => issue.Code == "players.count");
    }

    [Theory]
    [InlineData(29)]
    [InlineData(45)]
    [InlineData(121)]
    public void Validate_RejectsUnsupportedDuration(int duration)
    {
        var result = _validator.Validate(TestModels.ValidOneTimeRule() with { DurationMinutes = duration });

        Assert.Contains(result.Issues, static issue => issue.Code == "duration.invalid");
    }

    [Fact]
    public void Validate_RejectsInvalidCourtSetDuplicateTimesAndMissingTerms()
    {
        var rule = TestModels.ValidOneTimeRule() with
        {
            CourtOrder = [1, 2, 3, 5],
            StartTimes = [new TimeOnly(18, 0), new TimeOnly(18, 0)],
            TermsAuthorizedAtUtc = null,
        };

        var result = _validator.Validate(rule);

        Assert.Contains(result.Issues, static issue => issue.Code == "courts.invalid");
        Assert.Contains(result.Issues, static issue => issue.Code == "startTimes.duplicate");
        Assert.Contains(result.Issues, static issue => issue.Code == "terms.required");
    }

    [Fact]
    public void Validate_RejectsContradictoryRuleKinds()
    {
        var oneTime = TestModels.ValidOneTimeRule() with { Weekdays = [DayOfWeek.Monday] };
        var weekly = TestModels.ValidWeeklyRule() with { TargetDate = new DateOnly(2026, 7, 20) };

        Assert.Contains(_validator.Validate(oneTime).Issues, static issue => issue.Code == "weekdays.notAllowed");
        Assert.Contains(_validator.Validate(weekly).Issues, static issue => issue.Code == "targetDate.notAllowed");
    }
}
