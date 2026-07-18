namespace RockcliffeCourtBooker.Core;

public sealed record AppSettings
{
    public string? ExternalConfigurationPath { get; init; }

    public bool UnattendedSubmissionEnabled { get; init; }

    public bool VisibleBrowserByDefault { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
