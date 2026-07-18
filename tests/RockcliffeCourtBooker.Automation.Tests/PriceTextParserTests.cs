using RockcliffeCourtBooker.Automation.Parsing;

namespace RockcliffeCourtBooker.Automation.Tests;

public sealed class PriceTextParserTests
{
    [Theory]
    [InlineData("Price for the full court: $ 0.00")]
    [InlineData("Summary Price for the full court: CAD $0.00")]
    [InlineData("Price for the full court: $ 0.00\nPrice for the full court: $0.00")]
    public void ParsesAnUnambiguousZeroTotal(string text)
    {
        var parsed = PriceTextParser.TryParseDisplayedTotal(text, out var total);

        Assert.True(parsed);
        Assert.Equal(decimal.Zero, total);
    }

    [Fact]
    public void ParsesNonZeroTotalWithoutTreatingItAsFree()
    {
        var parsed = PriceTextParser.TryParseDisplayedTotal(
            "Price for the full court: $ 62.73",
            out var total);

        Assert.True(parsed);
        Assert.Equal(62.73m, total);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Total: $0.00")]
    [InlineData("Price for the full court: $0.00\nPrice for the full court: $62.73")]
    [InlineData("Price for the full court: free")]
    [InlineData("Price for the full court: $0")]
    [InlineData("Price for the full court: $0.0")]
    public void RejectsMissingMalformedOrConflictingTotals(string text)
    {
        Assert.False(PriceTextParser.TryParseDisplayedTotal(text, out _));
    }
}
