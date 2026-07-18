using Microsoft.Playwright;
using RockcliffeCourtBooker.Automation.Contracts;
using RockcliffeCourtBooker.Automation.Matchpoint;
using RockcliffeCourtBooker.Automation.Planning;
using RockcliffeCourtBooker.Automation.Security;

namespace RockcliffeCourtBooker.Automation;

public sealed class MatchpointDiagnosticService
{
    private readonly Func<Task<IPlaywright>> _playwrightFactory;

    public MatchpointDiagnosticService()
        : this(Microsoft.Playwright.Playwright.CreateAsync)
    {
    }

    internal MatchpointDiagnosticService(Func<Task<IPlaywright>> playwrightFactory)
    {
        _playwrightFactory = playwrightFactory;
    }

    public async Task<MatchpointDiagnosticResult> TestLoginAsync(
        MatchpointCredentials credentials,
        bool visibleBrowser,
        string? chromiumExecutablePath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        return await RunAsync(
            credentials,
            visibleBrowser,
            chromiumExecutablePath,
            async matchpoint =>
            {
                await matchpoint.LogInAsync(credentials, cancellationToken);
                return new MatchpointDiagnosticResult(
                    true,
                    BookingAutomationStatus.ReadOnlyComplete,
                    "Matchpoint login succeeded.");
            },
            cancellationToken);
    }

    public async Task<MatchpointDiagnosticResult> VerifyConfiguredPlayersAsync(
        MatchpointCredentials credentials,
        DateOnly targetDate,
        IReadOnlyList<string> expectedDisplayNames,
        bool visibleBrowser,
        string? chromiumExecutablePath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(expectedDisplayNames);
        if (expectedDisplayNames.Count == 0 ||
            expectedDisplayNames.Any(string.IsNullOrWhiteSpace) ||
            expectedDisplayNames.Distinct(StringComparer.Ordinal).Count() != expectedDisplayNames.Count)
        {
            return new MatchpointDiagnosticResult(
                false,
                BookingAutomationStatus.ValidationFailed,
                "Player verification requires unique, non-empty exact display names.");
        }

        return await RunAsync(
            credentials,
            visibleBrowser,
            chromiumExecutablePath,
            async matchpoint =>
            {
                await matchpoint.LogInAsync(credentials, cancellationToken);
                await matchpoint.OpenGridAsync(BookingKind.Doubles, targetDate);
                var startTimes = Enumerable.Range(0, 26)
                    .Select(index => new TimeOnly(8, 0).AddMinutes(index * 30))
                    .ToArray();
                var candidates = CandidatePlanner.Build(startTimes, [1, 2, 3, 4]);
                foreach (var candidate in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await matchpoint.OpenDurationAsync(candidate, BookingKind.Doubles, 30);
                        await matchpoint.OpenPlayerSelectionAsync();
                        await matchpoint.VerifyExactPlayersPresentAsync(expectedDisplayNames);
                        return new MatchpointDiagnosticResult(
                            true,
                            BookingAutomationStatus.ReadOnlyComplete,
                            $"Verified {expectedDisplayNames.Count} configured player(s) on Matchpoint.");
                    }
                    catch (CandidateUnavailableException)
                    {
                        await matchpoint.NavigateBackToGridAsync(BookingKind.Doubles, targetDate);
                    }
                }

                return new MatchpointDiagnosticResult(
                    false,
                    BookingAutomationStatus.NoAvailability,
                    "No available 30-minute clay-court checkout was found, so the Matchpoint player screen could not be inspected.");
            },
            cancellationToken);
    }

    private async Task<MatchpointDiagnosticResult> RunAsync(
        MatchpointCredentials credentials,
        bool visibleBrowser,
        string? chromiumExecutablePath,
        Func<MatchpointPage, Task<MatchpointDiagnosticResult>> operation,
        CancellationToken cancellationToken)
    {
        var redactor = new SensitiveDataRedactor(credentials.Username, credentials.Password);
        if (!PlaywrightRuntime.ConfigureBundledBrowserPath(chromiumExecutablePath))
        {
            return new MatchpointDiagnosticResult(
                false,
                BookingAutomationStatus.UnexpectedError,
                "The bundled Playwright Chromium installation is missing; no browser was opened.");
        }

        using var playwright = await _playwrightFactory();
        IBrowser? browser = null;
        try
        {
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = !visibleBrowser,
                ExecutablePath = chromiumExecutablePath,
            });
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                Locale = "en-CA",
                TimezoneId = "America/Toronto",
            });
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(10_000);
            page.SetDefaultNavigationTimeout(20_000);
            return await operation(new MatchpointPage(page));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new MatchpointDiagnosticResult(
                false,
                BookingAutomationStatus.Cancelled,
                "The Matchpoint diagnostic was cancelled.");
        }
        catch (MatchpointAutomationException exception)
        {
            return new MatchpointDiagnosticResult(
                false,
                exception.Status,
                redactor.Redact(exception.Message));
        }
        catch (PlaywrightException exception)
        {
            return new MatchpointDiagnosticResult(
                false,
                BookingAutomationStatus.PageStructureChanged,
                $"The Matchpoint diagnostic could not safely continue: {redactor.Redact(exception.Message)}");
        }
        finally
        {
            if (browser is not null)
            {
                await browser.CloseAsync();
            }
        }
    }
}
