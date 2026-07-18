using RockcliffeCourtBooker.Automation.Contracts;

namespace RockcliffeCourtBooker.Automation.Validation;

public static class BookingRequestValidator
{
    private static readonly HashSet<int> AllowedDurations = [30, 60, 90, 120];
    private static readonly HashSet<int> ClayCourts = [1, 2, 3, 4];

    public static IReadOnlyList<string> Validate(MatchpointBookingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.AttemptId))
        {
            errors.Add("AttemptId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Credentials.Username) ||
            string.IsNullOrWhiteSpace(request.Credentials.Password))
        {
            errors.Add("Matchpoint username and password are required.");
        }

        if (string.IsNullOrWhiteSpace(request.AccountMemberDisplayName))
        {
            errors.Add("The account member display name is required for checkout roster verification.");
        }

        if (request.OrderedStartTimes.Count == 0)
        {
            errors.Add("At least one requested start time is required.");
        }

        if (request.OrderedStartTimes.Count != request.OrderedStartTimes.Distinct().Count())
        {
            errors.Add("Requested start times must be unique.");
        }

        if (!AllowedDurations.Contains(request.DurationMinutes))
        {
            errors.Add("Duration must be 30, 60, 90, or 120 minutes.");
        }

        var expectedPartnerCount = request.BookingKind == BookingKind.Singles ? 1 : 3;
        if (request.PartnerDisplayNames.Count != expectedPartnerCount)
        {
            errors.Add($"{request.BookingKind} requires exactly {expectedPartnerCount} playing partner(s).");
        }

        if (request.PartnerDisplayNames.Any(string.IsNullOrWhiteSpace) ||
            request.PartnerDisplayNames.Distinct(StringComparer.Ordinal).Count() != request.PartnerDisplayNames.Count)
        {
            errors.Add("Playing partner names must be non-empty and unique.");
        }


        if (request.PartnerDisplayNames.Contains(request.AccountMemberDisplayName, StringComparer.Ordinal))
        {
            errors.Add("The account member cannot also be listed as a playing partner.");
        }

        if (request.CourtOrder.Count != 4 ||
            request.CourtOrder.Distinct().Count() != 4 ||
            !request.CourtOrder.ToHashSet().SetEquals(ClayCourts))
        {
            errors.Add("Court order must contain each clay court 1, 2, 3, and 4 exactly once.");
        }

        if (request.Mode == AutomationMode.Submit && !request.TermsAuthorized)
        {
            errors.Add("Terms authorization is required before a booking can be submitted.");
        }

        if (request.DiagnosticsDirectory is { Length: > 0 } diagnosticsDirectory &&
            !Path.IsPathFullyQualified(diagnosticsDirectory))
        {
            errors.Add("DiagnosticsDirectory must be an absolute path.");
        }

        return errors;
    }
}
