namespace RockcliffeCourtBooker.Core;

public sealed record ValidationIssue(string Code, string Message, string? Field = null);

public sealed class ValidationResult
{
    public static ValidationResult Success { get; } = new([]);

    public ValidationResult(IReadOnlyList<ValidationIssue> issues)
    {
        Issues = issues;
    }

    public IReadOnlyList<ValidationIssue> Issues { get; }

    public bool IsValid => Issues.Count == 0;

    public void ThrowIfInvalid()
    {
        if (!IsValid)
        {
            throw new BookingRuleValidationException(Issues);
        }
    }
}

public sealed class BookingRuleValidationException : Exception
{
    public BookingRuleValidationException(IReadOnlyList<ValidationIssue> issues)
        : base(string.Join(" ", issues.Select(static issue => issue.Message)))
    {
        Issues = issues;
    }

    public IReadOnlyList<ValidationIssue> Issues { get; }
}
