using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace RockcliffeCourtBooker.Core;

public sealed partial class PriceParser
{
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "The parser has an instance API for dependency injection.")]
    public bool TryParse(string? text, out decimal amount)
    {
        amount = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Replace('\u00a0', ' ').Replace('\u202f', ' ');
        var matches = DollarAmountRegex().Matches(normalized);
        if (matches.Count != 1)
        {
            return false;
        }

        var value = matches[0].Groups["amount"].Value.Replace(",", string.Empty, StringComparison.Ordinal);
        return decimal.TryParse(
            value,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out amount);
    }

    public bool IsExactlyZero(string? text) => TryParse(text, out var amount) && amount == decimal.Zero;

    [GeneratedRegex(@"(?<![-+\d.])\$\s*(?<amount>(?:0|[1-9]\d{0,2}(?:,\d{3})*)(?:\.\d{2}))(?![\d.])", RegexOptions.CultureInvariant)]
    private static partial Regex DollarAmountRegex();
}
