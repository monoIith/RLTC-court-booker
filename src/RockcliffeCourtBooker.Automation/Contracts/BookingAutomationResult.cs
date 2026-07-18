namespace RockcliffeCourtBooker.Automation.Contracts;

public sealed record BookingAutomationResult
{
    public required string AttemptId { get; init; }

    public required BookingAutomationStatus Status { get; init; }

    public required string Message { get; init; }

    public int? CourtNumber { get; init; }

    public TimeOnly? StartTime { get; init; }

    public IReadOnlyList<BookingCandidate> AvailableCandidates { get; init; } = [];

    public string? ScreenshotPath { get; init; }

    public string? TracePath { get; init; }

    public DateTimeOffset CompletedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public bool BookingWasSubmitted => Status == BookingAutomationStatus.Succeeded;
}
