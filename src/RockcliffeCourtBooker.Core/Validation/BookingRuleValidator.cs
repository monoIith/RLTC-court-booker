using System.Diagnostics.CodeAnalysis;

namespace RockcliffeCourtBooker.Core;

public sealed class BookingRuleValidator
{
    private static readonly HashSet<int> AllowedDurations = [30, 60, 90, 120];

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "The service has an instance API for dependency injection and future policy configuration.")]
    public ValidationResult Validate(BookingRule? rule)
    {
        if (rule is null)
        {
            return new ValidationResult([new("rule.required", "A booking rule is required.")]);
        }

        var issues = new List<ValidationIssue>();

        if (rule.Id == Guid.Empty)
        {
            issues.Add(new("id.required", "The booking rule ID cannot be empty.", nameof(rule.Id)));
        }

        if (string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Trim().Length > 120)
        {
            issues.Add(new("name.invalid", "The rule name must contain 1 to 120 characters.", nameof(rule.Name)));
        }

        if (!Enum.IsDefined(rule.Kind))
        {
            issues.Add(new("kind.invalid", "The rule kind is not supported.", nameof(rule.Kind)));
        }
        else if (rule.Kind == BookingRuleKind.OneTime)
        {
            if (rule.TargetDate is null)
            {
                issues.Add(new("targetDate.required", "A one-time rule requires a target date.", nameof(rule.TargetDate)));
            }

            if (rule.Weekdays.Count != 0)
            {
                issues.Add(new("weekdays.notAllowed", "A one-time rule cannot contain recurring weekdays.", nameof(rule.Weekdays)));
            }
        }
        else if (rule.Kind == BookingRuleKind.Weekly)
        {
            if (rule.TargetDate is not null)
            {
                issues.Add(new("targetDate.notAllowed", "A weekly rule cannot contain a one-time target date.", nameof(rule.TargetDate)));
            }

            if (rule.Weekdays.Count == 0 || rule.Weekdays.Any(static day => !Enum.IsDefined(day)))
            {
                issues.Add(new("weekdays.required", "A weekly rule requires at least one valid weekday.", nameof(rule.Weekdays)));
            }
            else if (rule.Weekdays.Distinct().Count() != rule.Weekdays.Count)
            {
                issues.Add(new("weekdays.duplicate", "Recurring weekdays must be unique.", nameof(rule.Weekdays)));
            }
        }

        ValidateTimes(rule.StartTimes, issues);

        if (!Enum.IsDefined(rule.BookingType))
        {
            issues.Add(new("bookingType.invalid", "The booking type is not supported.", nameof(rule.BookingType)));
        }

        if (!AllowedDurations.Contains(rule.DurationMinutes))
        {
            issues.Add(new("duration.invalid", "The duration must be 30, 60, 90, or 120 minutes.", nameof(rule.DurationMinutes)));
        }

        ValidatePlayers(rule, issues);
        ValidateCourts(rule.CourtOrder, issues);

        if (rule.Enabled && rule.TermsAuthorizedAtUtc is null)
        {
            issues.Add(new("terms.required", "An enabled booking rule requires recorded terms authorization.", nameof(rule.TermsAuthorizedAtUtc)));
        }

        if (rule.TermsAuthorizedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            issues.Add(new("terms.future", "Terms authorization cannot be recorded in the future.", nameof(rule.TermsAuthorizedAtUtc)));
        }

        if (rule.CreatedAtUtc > rule.UpdatedAtUtc)
        {
            issues.Add(new("timestamps.invalid", "The update timestamp cannot precede the creation timestamp."));
        }

        return issues.Count == 0 ? ValidationResult.Success : new ValidationResult(issues);
    }

    private static void ValidateTimes(IReadOnlyList<TimeOnly> times, List<ValidationIssue> issues)
    {
        if (times.Count == 0)
        {
            issues.Add(new("startTimes.required", "At least one preferred start time is required.", nameof(BookingRule.StartTimes)));
            return;
        }

        if (times.Distinct().Count() != times.Count)
        {
            issues.Add(new("startTimes.duplicate", "Preferred start times must be unique.", nameof(BookingRule.StartTimes)));
        }

        if (times.Any(static time => time.Second != 0 || time.Millisecond != 0 || (time.Minute != 0 && time.Minute != 30)))
        {
            issues.Add(new("startTimes.increment", "Start times must be on a whole- or half-hour boundary.", nameof(BookingRule.StartTimes)));
        }
    }

    private static void ValidatePlayers(BookingRule rule, List<ValidationIssue> issues)
    {
        if (rule.PlayerIds.Any(string.IsNullOrWhiteSpace) ||
            rule.PlayerIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != rule.PlayerIds.Count)
        {
            issues.Add(new("players.invalid", "Player IDs must be non-empty and unique.", nameof(rule.PlayerIds)));
        }

        var expected = rule.BookingType switch
        {
            BookingType.Singles => 1,
            BookingType.Doubles => 3,
            _ => -1,
        };

        if (expected >= 0 && rule.PlayerIds.Count != expected)
        {
            issues.Add(new(
                "players.count",
                $"{rule.BookingType} requires exactly {expected} additional player(s).",
                nameof(rule.PlayerIds)));
        }
    }

    private static void ValidateCourts(IReadOnlyList<int> courts, List<ValidationIssue> issues)
    {
        if (courts.Count != 4 || courts.Distinct().Count() != 4 || courts.Any(static court => court is < 1 or > 4))
        {
            issues.Add(new("courts.invalid", "Court order must contain each clay court 1, 2, 3, and 4 exactly once.", nameof(BookingRule.CourtOrder)));
        }
    }
}
