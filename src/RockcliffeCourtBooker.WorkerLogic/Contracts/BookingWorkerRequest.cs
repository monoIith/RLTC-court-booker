using RockcliffeCourtBooker.Automation.Contracts;

namespace RockcliffeCourtBooker.Worker.Contracts;

public sealed record BookingWorkerRequest
{
    public int SchemaVersion { get; init; } = 1;

    public required Guid AttemptId { get; init; }

    public required Guid RuleId { get; init; }

    public required string AccountConfigurationPath { get; init; }

    public required DateOnly TargetDate { get; init; }

    public required IReadOnlyList<TimeOnly> OrderedStartTimes { get; init; }

    public required BookingKind BookingKind { get; init; }

    public required int DurationMinutes { get; init; }

    public required IReadOnlyList<string> PlayerIds { get; init; }

    public required IReadOnlyList<int> CourtOrder { get; init; }

    public required AutomationMode Mode { get; init; }

    public bool TermsAuthorized { get; init; }

    public bool VisibleBrowser { get; init; }

    public bool UserInitiated { get; init; }

    public string? DiagnosticsDirectory { get; init; }

    public string? ChromiumExecutablePath { get; init; }
}
