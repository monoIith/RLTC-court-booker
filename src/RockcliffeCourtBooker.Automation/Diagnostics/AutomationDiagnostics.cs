using Microsoft.Playwright;
using System.Text.RegularExpressions;

namespace RockcliffeCourtBooker.Automation.Diagnostics;

internal sealed class AutomationDiagnostics
{
    private readonly string? _attemptDirectory;
    private bool _traceStarted;
    private bool _traceStopped;

    public AutomationDiagnostics(string? diagnosticsRoot, string attemptId)
    {
        if (string.IsNullOrWhiteSpace(diagnosticsRoot))
        {
            return;
        }

        var invalidCharacters = Path.GetInvalidFileNameChars();
        var safeAttemptId = string.Concat(
            attemptId.Select(character => invalidCharacters.Contains(character) ? '_' : character));
        if (string.IsNullOrWhiteSpace(safeAttemptId))
        {
            safeAttemptId = Guid.NewGuid().ToString("N");
        }

        _attemptDirectory = Path.Combine(
            Path.GetFullPath(diagnosticsRoot),
            $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{safeAttemptId}");
    }

    public async Task StartTraceAsync(IBrowserContext context)
    {
        if (_attemptDirectory is null || _traceStarted)
        {
            return;
        }

        await context.Tracing.StartAsync(new TracingStartOptions
        {
            Screenshots = true,
            Snapshots = true,
            Sources = false,
            Title = "Rockcliffe booking attempt (credentials excluded)",
        });
        _traceStarted = true;
    }

    public async Task<DiagnosticArtifacts> CompleteAsync(
        IBrowserContext context,
        IPage page,
        bool retain,
        Func<Task> clearSensitiveInputs)
    {
        if (_traceStopped)
        {
            return new DiagnosticArtifacts(null, null);
        }

        string? screenshotPath = null;
        string? tracePath = null;
        if (retain && _attemptDirectory is not null)
        {
            Directory.CreateDirectory(_attemptDirectory);
            await clearSensitiveInputs();
            screenshotPath = Path.Combine(_attemptDirectory, "failure.png");
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = screenshotPath,
                FullPage = true,
            });
        }

        if (_traceStarted)
        {
            if (retain && _attemptDirectory is not null)
            {
                tracePath = Path.Combine(_attemptDirectory, "trace.zip");
                await context.Tracing.StopAsync(new TracingStopOptions { Path = tracePath });
            }
            else
            {
                await context.Tracing.StopAsync();
            }
        }

        _traceStopped = true;
        return new DiagnosticArtifacts(screenshotPath, tracePath);
    }

    public static void DeleteExpiredArtifacts(string? diagnosticsRoot, TimeSpan retention)
    {
        if (string.IsNullOrWhiteSpace(diagnosticsRoot) || !Directory.Exists(diagnosticsRoot))
        {
            return;
        }

        var cutoff = DateTimeOffset.UtcNow.Subtract(retention).UtcDateTime;
        foreach (var directory in Directory.EnumerateDirectories(diagnosticsRoot))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(directory) < cutoff &&
                    IsManagedAttemptDirectory(directory))
                {
                    DeleteIfPresent(Path.Combine(directory, "failure.png"));
                    DeleteIfPresent(Path.Combine(directory, "trace.zip"));

                    // Never recursively delete diagnostics. A directory containing an
                    // unexpected file is left in place for the user to inspect.
                    Directory.Delete(directory, recursive: false);
                }
            }
            catch (IOException)
            {
                // A diagnostic viewer or another worker may have the artifact open.
            }
            catch (UnauthorizedAccessException)
            {
                // Retention is best-effort and must not prevent a booking attempt.
            }
        }
    }

    private static bool IsManagedAttemptDirectory(string directory)
    {
        var name = Path.GetFileName(directory);
        return Regex.IsMatch(
            name,
            @"^\d{8}-\d{6}-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

internal sealed record DiagnosticArtifacts(string? ScreenshotPath, string? TracePath);
