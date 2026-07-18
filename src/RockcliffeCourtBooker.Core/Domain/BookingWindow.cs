namespace RockcliffeCourtBooker.Core;

public readonly record struct BookingWindow(
    DateOnly TargetDate,
    DateTimeOffset WorkerStartsAtUtc,
    DateTimeOffset OpensAtUtc,
    DateTimeOffset UnattendedCutoffAtUtc);
