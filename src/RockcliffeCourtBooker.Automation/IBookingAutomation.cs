using RockcliffeCourtBooker.Automation.Contracts;

namespace RockcliffeCourtBooker.Automation;

public interface IBookingAutomation
{
    Task<BookingAutomationResult> ExecuteAsync(
        MatchpointBookingRequest request,
        CancellationToken cancellationToken = default);
}
