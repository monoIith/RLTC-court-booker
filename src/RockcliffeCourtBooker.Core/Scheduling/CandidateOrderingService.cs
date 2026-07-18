using System.Diagnostics.CodeAnalysis;

namespace RockcliffeCourtBooker.Core;

public sealed class CandidateOrderingService
{
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "The service has an instance API for dependency injection.")]
    public IReadOnlyList<BookingCandidate> CreateCandidates(
        IReadOnlyList<TimeOnly> startTimes,
        IReadOnlyList<int> courtOrder)
    {
        ArgumentNullException.ThrowIfNull(startTimes);
        ArgumentNullException.ThrowIfNull(courtOrder);

        if (startTimes.Count == 0)
        {
            throw new ArgumentException("At least one start time is required.", nameof(startTimes));
        }

        if (startTimes.Distinct().Count() != startTimes.Count)
        {
            throw new ArgumentException("Start times must be unique.", nameof(startTimes));
        }

        if (courtOrder.Count != 4 || courtOrder.Distinct().Count() != 4 || courtOrder.Any(static court => court is < 1 or > 4))
        {
            throw new ArgumentException("Court order must contain each clay court 1 through 4 exactly once.", nameof(courtOrder));
        }

        return startTimes
            .SelectMany(time => courtOrder.Select(court => new BookingCandidate(time, court)))
            .ToArray();
    }

    public IReadOnlyList<BookingCandidate> CreateCandidates(BookingRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return CreateCandidates(rule.StartTimes, rule.CourtOrder);
    }
}
