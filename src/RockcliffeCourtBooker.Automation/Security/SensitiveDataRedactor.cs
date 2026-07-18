using System.Text.RegularExpressions;

namespace RockcliffeCourtBooker.Automation.Security;

public sealed partial class SensitiveDataRedactor
{
    private readonly string[] _literalSecrets;

    public SensitiveDataRedactor(params IEnumerable<string> literalSecrets)
    {
        _literalSecrets = literalSecrets
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ToArray();
    }

    public string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var redacted = value;
        foreach (var secret in _literalSecrets)
        {
            redacted = redacted.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }

        redacted = JsonPasswordPattern().Replace(redacted, "$1[REDACTED]$2");
        redacted = QueryPasswordPattern().Replace(redacted, "$1[REDACTED]");
        return redacted;
    }

    [GeneratedRegex("(\\\"password\\\"\\s*:\\s*\\\")[^\\\"]*(\\\")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JsonPasswordPattern();

    [GeneratedRegex("((?:password|passwd|pwd)=)[^&\\s]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QueryPasswordPattern();
}
