using RockcliffeCourtBooker.Automation.Contracts;
using RockcliffeCourtBooker.Automation.Planning;

namespace RockcliffeCourtBooker.Automation.Tests;

public sealed class CandidatePlannerTests
{
    [Fact]
    public void OrdersByRequestedTimeThenCourtPreference()
    {
        var firstTime = new TimeOnly(18, 0);
        var secondTime = new TimeOnly(19, 30);

        var candidates = CandidatePlanner.Build([firstTime, secondTime], [3, 1, 4, 2]);

        Assert.Equal(
            [
                new BookingCandidate(firstTime, 3),
                new BookingCandidate(firstTime, 1),
                new BookingCandidate(firstTime, 4),
                new BookingCandidate(firstTime, 2),
                new BookingCandidate(secondTime, 3),
                new BookingCandidate(secondTime, 1),
                new BookingCandidate(secondTime, 4),
                new BookingCandidate(secondTime, 2),
            ],
            candidates);
    }
}
