using RockcliffeCourtBooker.Automation.Contracts;

namespace RockcliffeCourtBooker.Automation.Matchpoint;

internal sealed class MatchpointAutomationException : Exception
{
    public MatchpointAutomationException(BookingAutomationStatus status, string message)
        : base(message)
    {
        Status = status;
    }

    public BookingAutomationStatus Status { get; }
}

internal sealed class CandidateUnavailableException : Exception
{
    public CandidateUnavailableException(string message)
        : base(message)
    {
    }
}
