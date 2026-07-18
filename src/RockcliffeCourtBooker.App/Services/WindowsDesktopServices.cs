using System.Diagnostics;
using Microsoft.Playwright;
using Microsoft.Win32;
using RockcliffeCourtBooker.App.Models;
using RockcliffeCourtBooker.Automation;
using RockcliffeCourtBooker.Automation.Contracts;
using RockcliffeCourtBooker.Core;

namespace RockcliffeCourtBooker.App.Services;

public sealed class WindowsJsonFilePickerService : IFilePickerService
{
    public string? PickJsonFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose Matchpoint account configuration",
            Filter = "JSON configuration (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json",
            CheckFileExists = true,
            Multiselect = false
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}

public sealed class WindowsUserDialogService : IUserDialogService
{
    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void ShowError(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}

public sealed class WindowsExternalLauncher : IExternalLauncher
{
    public OperationResult Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return OperationResult.Failure("The diagnostic file no longer exists.");
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return OperationResult.Success("Opened diagnostic file.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return OperationResult.Failure("Windows could not open the diagnostic file.");
        }
    }
}

public sealed class PlaywrightBrowserDiagnosticsService(IExternalConfigurationLoader configurationLoader)
    : IBrowserDiagnosticsService
{
    public async Task<OperationResult> TestLoginAsync(
        string configurationPath,
        bool visibleBrowser,
        CancellationToken cancellationToken = default)
    {
        var loaded = await TryLoadAsync(configurationPath, cancellationToken);
        if (loaded.Result is { } failure)
        {
            return failure;
        }

        var configuration = loaded.Configuration!;
        try
        {
            var diagnostic = await new MatchpointDiagnosticService().TestLoginAsync(
                ToCredentials(configuration),
                visibleBrowser,
                chromiumExecutablePath: null,
                cancellationToken);
            return diagnostic.Succeeded
                ? OperationResult.Success(diagnostic.Message)
                : OperationResult.Failure(diagnostic.Message);
        }
        catch (Exception exception) when (exception is PlaywrightException or InvalidOperationException or IOException)
        {
            return OperationResult.Failure(
                "The bundled diagnostic browser could not be started. No website changes were made.");
        }
    }

    public async Task<OperationResult> VerifyPlayersAsync(
        string configurationPath,
        bool visibleBrowser,
        CancellationToken cancellationToken = default)
    {
        var loaded = await TryLoadAsync(configurationPath, cancellationToken);
        if (loaded.Result is { } failure)
        {
            return failure;
        }

        var configuration = loaded.Configuration!;
        var timeZone = TorontoTimeZone.GetSystemTimeZone();
        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
        var targetDate = DateOnly.FromDateTime(localNow.DateTime);
        try
        {
            var diagnostic = await new MatchpointDiagnosticService().VerifyConfiguredPlayersAsync(
                ToCredentials(configuration),
                targetDate,
                configuration.Players.Select(static player => player.DisplayName).ToArray(),
                visibleBrowser,
                chromiumExecutablePath: null,
                cancellationToken);
            return diagnostic.Succeeded
                ? OperationResult.Success(diagnostic.Message)
                : OperationResult.Failure(diagnostic.Message);
        }
        catch (Exception exception) when (exception is PlaywrightException or InvalidOperationException or IOException)
        {
            return OperationResult.Failure(
                "The bundled diagnostic browser could not be started. No website changes were made.");
        }
    }

    private async Task<(ExternalConfiguration? Configuration, OperationResult? Result)> TryLoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var loaded = await configurationLoader.LoadAsync(path, cancellationToken);
            return (loaded.Configuration, null);
        }
        catch (ExternalConfigurationException exception)
        {
            return (null, OperationResult.Failure(
                $"The external account/player file failed validation ({exception.Code}); no browser was opened."));
        }
    }

    private static MatchpointCredentials ToCredentials(ExternalConfiguration configuration) => new()
    {
        Username = configuration.Account.Username,
        Password = configuration.Account.Password,
    };
}
