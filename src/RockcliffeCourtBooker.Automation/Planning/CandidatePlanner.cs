using RockcliffeCourtBooker.Automation.Contracts;

namespace RockcliffeCourtBooker.Automation.Planning;

public static class CandidatePlanner
{
    public static IReadOnlyList<BookingCandidate> Build(
        IReadOnlyList<TimeOnly> orderedStartTimes,
        IReadOnlyList<int> courtOrder)
    {
        ArgumentNullException.ThrowIfNull(orderedStartTimes);
        ArgumentNullException.ThrowIfNull(courtOrder);

        return orderedStartTimes
            .SelectMany(time => courtOrder.Select(court => new BookingCandidate(time, court)))
            .ToArray();
    }
}
