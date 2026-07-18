namespace RockcliffeCourtBooker.Automation.Contracts;

/// <summary>
/// In-memory credentials. Callers must never serialize this object into logs, results, or persistence.
/// </summary>
public sealed class MatchpointCredentials
{
    public required string Username { get; init; }

    public required string Password { get; init; }

    public override string ToString() => "MatchpointCredentials { Username = [REDACTED], Password = [REDACTED] }";
}
