namespace RockcliffeCourtBooker.Automation.Contracts;

public sealed record MatchpointDiagnosticResult(
    bool Succeeded,
    BookingAutomationStatus Status,
    string Message);
