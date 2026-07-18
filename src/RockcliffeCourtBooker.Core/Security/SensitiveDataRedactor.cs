namespace RockcliffeCourtBooker.Core;

public static class SensitiveDataRedactor
{
    public const string RedactedValue = "[REDACTED]";

    public static string? Redact(string? text, params IEnumerable<string?> secrets)
    {
        if (text is null)
        {
            return null;
        }

        var result = text;
        foreach (var secret in secrets
                     .Where(static secret => !string.IsNullOrEmpty(secret))
                     .Distinct(StringComparer.Ordinal)
                     .OrderByDescending(static secret => secret!.Length))
        {
            result = result.Replace(secret!, RedactedValue, StringComparison.Ordinal);
        }

        return result;
    }
}
