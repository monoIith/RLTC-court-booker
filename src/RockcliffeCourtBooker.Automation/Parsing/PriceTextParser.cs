using System.Globalization;
using System.Text.RegularExpressions;

namespace RockcliffeCourtBooker.Automation.Parsing;

public static partial class PriceTextParser
{
    public static bool TryParseDisplayedTotal(string visibleText, out decimal total)
    {
        total = default;
        if (string.IsNullOrWhiteSpace(visibleText))
        {
            return false;
        }

        var matches = PricePattern().Matches(visibleText);
        if (matches.Count == 0)
        {
            return false;
        }

        decimal? parsedTotal = null;
        foreach (Match match in matches)
        {
            if (!decimal.TryParse(
                    match.Groups[1].Value.Replace(",", string.Empty, StringComparison.Ordinal),
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                return false;
            }

            if (parsedTotal is not null && parsedTotal.Value != value)
            {
                return false;
            }

            parsedTotal = value;
        }

        total = parsedTotal.GetValueOrDefault();
        return parsedTotal is not null;
    }

    [GeneratedRegex(@"Price\s+for\s+the\s+full\s+court\s*:\s*(?:CAD\s*)?\$\s*([0-9]+(?:,[0-9]{3})*\.[0-9]{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PricePattern();
}
