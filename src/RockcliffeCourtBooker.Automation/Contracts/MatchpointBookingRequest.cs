namespace RockcliffeCourtBooker.Automation.Contracts;

public sealed record MatchpointBookingRequest
{
    public required string AttemptId { get; init; }

    public required MatchpointCredentials Credentials { get; init; }

    public required string AccountMemberDisplayName { get; init; }

    public required DateOnly TargetDate { get; init; }

    public required IReadOnlyList<TimeOnly> OrderedStartTimes { get; init; }

    public required BookingKind BookingKind { get; init; }

    public required int DurationMinutes { get; init; }

    public required IReadOnlyList<string> PartnerDisplayNames { get; init; }

    public required IReadOnlyList<int> CourtOrder { get; init; }

    public required AutomationMode Mode { get; init; }

    public bool TermsAuthorized { get; init; }

    public bool Headless { get; init; } = true;

    public DateTimeOffset? BookingOpensAtUtc { get; init; }

    /// <summary>
    /// Optional hard boundary for starting the sole final submission action.
    /// The unattended worker sets this to the 8:15 AM cutoff; user-initiated
    /// attempts leave it unset.
    /// </summary>
    public DateTimeOffset? SubmissionCutoffAtUtc { get; init; }

    public string? ChromiumExecutablePath { get; init; }

    public string? DiagnosticsDirectory { get; init; }
}
