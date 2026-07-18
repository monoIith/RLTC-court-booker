namespace RockcliffeCourtBooker.Automation.Matchpoint;

internal static class CheckoutRosterValidator
{
    public static bool IsExactMatch(
        string accountMemberDisplayName,
        IReadOnlyList<string> partnerDisplayNames,
        IReadOnlyList<string> actualRoster)
    {
        ArgumentNullException.ThrowIfNull(partnerDisplayNames);
        ArgumentNullException.ThrowIfNull(actualRoster);

        var expectedRoster = new[] { accountMemberDisplayName }
            .Concat(partnerDisplayNames)
            .ToArray();
        return actualRoster.Count == expectedRoster.Length &&
               actualRoster.Distinct(StringComparer.Ordinal).Count() == actualRoster.Count &&
               expectedRoster.Distinct(StringComparer.Ordinal).Count() == expectedRoster.Length &&
               actualRoster.ToHashSet(StringComparer.Ordinal).SetEquals(expectedRoster);
    }
}
