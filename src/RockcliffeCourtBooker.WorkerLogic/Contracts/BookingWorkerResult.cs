using RockcliffeCourtBooker.Automation.Contracts;

namespace RockcliffeCourtBooker.Worker.Contracts;

public enum WorkerOutcome
{
    Completed,
    InvalidRequest,
    ConfigurationFailed,
    MissedWindow,
    AlreadyRunning,
    AutomationFailed,
}

public sealed record BookingWorkerResult
{
    public required Guid AttemptId { get; init; }

    public required WorkerOutcome Outcome { get; init; }

    public required string Message { get; init; }

    public BookingAutomationResult? AutomationResult { get; init; }

    public DateTimeOffset CompletedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
