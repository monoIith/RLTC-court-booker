namespace RockcliffeCourtBooker.Automation;

internal static class PlaywrightRuntime
{
    public static bool ConfigureBundledBrowserPath(string? explicitExecutablePath)
    {
        if (!string.IsNullOrWhiteSpace(explicitExecutablePath))
        {
            return Path.IsPathFullyQualified(explicitExecutablePath) && File.Exists(explicitExecutablePath);
        }

        // Installed runs must always prefer the Chromium tree shipped with the
        // application. The UI lives at the install root while the independently
        // self-contained worker lives one directory below it.
        var bundledPaths = ResolveBundledBrowserDirectories(AppContext.BaseDirectory);
        var bundledPath = bundledPaths.FirstOrDefault(Directory.Exists);
        if (bundledPath is not null)
        {
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", bundledPath);
            return true;
        }

        // Only the deterministic test process may opt into a process-scoped
        // browser directory before the release tree is staged. Production runs
        // never honor a machine-level Playwright override.
        if (!string.Equals(
                Environment.GetEnvironmentVariable("ROCKCLIFFE_RUN_PLAYWRIGHT_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            return false;
        }

        var configuredPath = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        return !string.IsNullOrWhiteSpace(configuredPath) &&
               Path.IsPathFullyQualified(configuredPath) &&
               Directory.Exists(configuredPath);
    }

    internal static IReadOnlyList<string> ResolveBundledBrowserDirectories(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var normalizedBase = Path.GetFullPath(baseDirectory);
        var directPath = Path.Combine(normalizedBase, "ms-playwright");
        if (!string.Equals(
                new DirectoryInfo(normalizedBase).Name,
                "worker",
                StringComparison.OrdinalIgnoreCase))
        {
            return [directPath];
        }

        return
        [
            directPath,
            Path.GetFullPath(Path.Combine(normalizedBase, "..", "ms-playwright")),
        ];
    }
}
