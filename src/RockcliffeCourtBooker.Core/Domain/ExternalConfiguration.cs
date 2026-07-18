using System.Diagnostics.CodeAnalysis;

namespace RockcliffeCourtBooker.Core;

public sealed class ExternalConfiguration
{
    public required int SchemaVersion { get; init; }

    public required ExternalAccount Account { get; init; }

    public required IReadOnlyList<ExternalPlayer> Players { get; init; }

    public override string ToString() =>
        $"ExternalConfiguration {{ SchemaVersion = {SchemaVersion}, Account = {Account}, Players = {Players.Count} player(s) }}";
}

public sealed class ExternalAccount
{
    public required string MemberName { get; init; }

    public required string Username { get; init; }

    [SuppressMessage("Security", "S2068:Hard-coded credentials are security-sensitive", Justification = "This runtime value comes from the user's external file.")]
    public required string Password { get; init; }

    public override string ToString() =>
        $"ExternalAccount {{ MemberName = {MemberName}, Username = {Username}, Password = [REDACTED] }}";
}

public sealed record ExternalPlayer(string Id, string DisplayName);

public sealed record ExternalConfigurationLoadResult(
    string SourcePath,
    ExternalConfiguration Configuration,
    IReadOnlyList<string> SecurityWarnings)
{
    public override string ToString() =>
        $"ExternalConfigurationLoadResult {{ SourcePath = {SourcePath}, Configuration = {Configuration}, SecurityWarnings = {SecurityWarnings.Count} warning(s) }}";
}

public sealed class ExternalConfigurationException : Exception
{
    public ExternalConfigurationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public ExternalConfigurationException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
