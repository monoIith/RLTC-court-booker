using RockcliffeCourtBooker.Automation.Contracts;
using RockcliffeCourtBooker.Automation.Validation;

namespace RockcliffeCourtBooker.Automation.Tests;

public sealed class BookingRequestValidatorTests
{
    [Fact]
    public void ValidSinglesRequestHasNoErrors()
    {
        var request = ValidRequest() with
        {
            BookingKind = BookingKind.Singles,
            PartnerDisplayNames = ["Partner One"],
        };

        Assert.Empty(BookingRequestValidator.Validate(request));
    }

    [Fact]
    public void DoublesRequiresExactlyThreePartners()
    {
        var request = ValidRequest() with { PartnerDisplayNames = ["Partner One", "Partner Two"] };

        var errors = BookingRequestValidator.Validate(request);

        Assert.Contains(errors, error => error.Contains("exactly 3", StringComparison.Ordinal));
    }

    [Fact]
    public void CourtOrderMustContainAllFourClayCourtsOnce()
    {
        var request = ValidRequest() with { CourtOrder = [1, 2, 3, 3] };

        var errors = BookingRequestValidator.Validate(request);

        Assert.Contains(errors, error => error.Contains("each clay court", StringComparison.Ordinal));
    }

    [Fact]
    public void SubmitRequiresTermsAuthorization()
    {
        var request = ValidRequest() with { TermsAuthorized = false };

        var errors = BookingRequestValidator.Validate(request);

        Assert.Contains(errors, error => error.Contains("Terms authorization", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(15)]
    [InlineData(45)]
    [InlineData(180)]
    public void UnsupportedDurationIsRejected(int durationMinutes)
    {
        var request = ValidRequest() with { DurationMinutes = durationMinutes };

        var errors = BookingRequestValidator.Validate(request);

        Assert.Contains(errors, error => error.Contains("Duration", StringComparison.Ordinal));
    }

    private static MatchpointBookingRequest ValidRequest()
    {
        return new MatchpointBookingRequest
        {
            AttemptId = "attempt-1",
            Credentials = new MatchpointCredentials
            {
                Username = "member",
                Password = "secret-value",
            },
            AccountMemberDisplayName = "Account Holder",
            TargetDate = new DateOnly(2026, 7, 20),
            OrderedStartTimes = [new TimeOnly(18, 0), new TimeOnly(19, 0)],
            BookingKind = BookingKind.Doubles,
            DurationMinutes = 90,
            PartnerDisplayNames = ["Partner One", "Partner Two", "Partner Three"],
            CourtOrder = [2, 1, 4, 3],
            Mode = AutomationMode.Submit,
            TermsAuthorized = true,
            DiagnosticsDirectory = Path.GetFullPath(Path.GetTempPath()),
        };
    }
}
