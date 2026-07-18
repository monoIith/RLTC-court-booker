using RockcliffeCourtBooker.Automation.Contracts;
using RockcliffeCourtBooker.Worker.Contracts;

namespace RockcliffeCourtBooker.Worker;

internal static class WorkerRequestValidator
{
    public static IReadOnlyList<string> Validate(BookingWorkerRequest request)
    {
        var errors = new List<string>();
        if (request.SchemaVersion != 1)
        {
            errors.Add("Only worker request schemaVersion 1 is supported.");
        }

        if (request.AttemptId == Guid.Empty || request.RuleId == Guid.Empty)
        {
            errors.Add("attemptId and ruleId must be non-empty UUIDs.");
        }

        if (string.IsNullOrWhiteSpace(request.AccountConfigurationPath) ||
            !Path.IsPathFullyQualified(request.AccountConfigurationPath))
        {
            errors.Add("accountConfigurationPath must be absolute.");
        }

        var expectedPlayers = request.BookingKind == BookingKind.Singles ? 1 : 3;
        if (request.PlayerIds.Count != expectedPlayers ||
            request.PlayerIds.Any(string.IsNullOrWhiteSpace) ||
            request.PlayerIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.PlayerIds.Count)
        {
            errors.Add($"{request.BookingKind} requires {expectedPlayers} unique configured player ID(s).");
        }

        if (request.Mode == AutomationMode.Submit && !request.TermsAuthorized)
        {
            errors.Add("termsAuthorized is required for submit mode.");
        }

        return errors;
    }
}
