using RockcliffeCourtBooker.Automation.Matchpoint;

namespace RockcliffeCourtBooker.Automation.Tests;

public sealed class CheckoutRosterValidatorTests
{
    private static readonly string[] Partners = ["Player One", "Player Two", "Player Three"];

    [Fact]
    public void AcceptsAccountHolderAndExactPartnersInAnyOrder()
    {
        Assert.True(CheckoutRosterValidator.IsExactMatch(
            "Account Holder",
            Partners,
            ["Player Two", "Account Holder", "Player Three", "Player One"]));
    }

    [Fact]
    public void RejectsMissingAccountHolder()
    {
        Assert.False(CheckoutRosterValidator.IsExactMatch(
            "Account Holder",
            Partners,
            ["Player One", "Player Two", "Player Three"]));
    }

    [Fact]
    public void RejectsExtraPlayer()
    {
        Assert.False(CheckoutRosterValidator.IsExactMatch(
            "Account Holder",
            Partners,
            ["Account Holder", "Player One", "Player Two", "Player Three", "Unexpected Player"]));
    }

    [Fact]
    public void RejectsDuplicateEvenWhenAllExpectedNamesArePresent()
    {
        Assert.False(CheckoutRosterValidator.IsExactMatch(
            "Account Holder",
            Partners,
            ["Account Holder", "Player One", "Player Two", "Player Three", "Player One"]));
    }

    [Fact]
    public void ComparisonIsExactAndCaseSensitive()
    {
        Assert.False(CheckoutRosterValidator.IsExactMatch(
            "Account Holder",
            Partners,
            ["Account Holder", "player one", "Player Two", "Player Three"]));
    }
}
