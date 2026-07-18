namespace RockcliffeCourtBooker.Automation;

internal static class PlaywrightRuntime
{
    public static bool ConfigureBundledBrowserPath(string? explicitExecutablePath)
    {
        if (!string.IsNullOrWhiteSpace(explicitExecutablePath))
        {
            return Path.IsPathFullyQualified(explicitExecutablePath) && File.Exists(explicitExecutablePath);
        }

        // Installed runs must always prefer the Chromium tree shipped beside the
        // application. This prevents a machine-level Playwright environment
        // variable from silently selecting a different browser revision.
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "ms-playwright");
        if (Directory.Exists(bundledPath))
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
}
