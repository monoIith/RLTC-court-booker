using RockcliffeCourtBooker.App.Models;
using RockcliffeCourtBooker.Core;

namespace RockcliffeCourtBooker.App.Services;

public sealed class ExternalJsonConfigurationService(
    IExternalConfigurationLoader loader,
    IBookingRepository repository) : IConfigurationFileService
{
    private const string PlaintextWarning =
        "This file contains a plaintext password. Keep it in a private folder and never commit, sync, email, or share it.";

    public async Task<ConfigurationValidationResult> ValidateAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var loaded = await loader.LoadAsync(filePath, cancellationToken);
            var settings = await repository.GetSettingsAsync(cancellationToken);
            await repository.SaveSettingsAsync(
                settings with
                {
                    ExternalConfigurationPath = loaded.SourcePath,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                },
                cancellationToken);

            var configuration = loaded.Configuration;
            var warnings = loaded.SecurityWarnings.Count == 0
                ? PlaintextWarning
                : $"{PlaintextWarning} {string.Join(" ", loaded.SecurityWarnings)}";
            var summary = new ConfigurationSummary(
                loaded.SourcePath,
                configuration.Account.MemberName,
                configuration.Account.Username,
                configuration.Players
                    .Select(static player => new PlayerSummary(player.Id, player.DisplayName))
                    .OrderBy(static player => player.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray(),
                warnings);

            return ConfigurationValidationResult.Valid(
                summary,
                $"Configuration is valid. Found {configuration.Players.Count} selectable " +
                (configuration.Players.Count == 1 ? "player." : "players."));
        }
        catch (ExternalConfigurationException exception)
        {
            return ConfigurationValidationResult.Invalid(ToFriendlyMessage(exception.Code));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ConfigurationValidationResult.Invalid(
                "The configuration could not be saved locally. Check access to the application's data folder.");
        }
    }

    private static string ToFriendlyMessage(string code) => code switch
    {
        "path.required" => "Choose an account JSON file first.",
        "path.absolute" => "The configuration path must be absolute.",
        "path.managed" => "Store the account JSON outside the app's LocalAppData folder so upgrades and uninstall can never remove it.",
        "file.missing" => "The configuration file could not be found. It may have been moved or deleted.",
        "file.size" => "The configuration file is empty or unexpectedly large.",
        "file.unreadable" => "The configuration file could not be read. Check its permissions and try again.",
        "json.invalid" => "The configuration file is not valid strict JSON.",
        "schemaVersion.unsupported" => "Only account configuration schemaVersion 1 is supported.",
        "players.required" => "Add at least one selectable player to the players array.",
        "players.idDuplicate" => "Player IDs must be unique, ignoring letter case.",
        "players.nameDuplicate" => "Player display names must be unique, ignoring letter case.",
        _ => "The configuration does not match the required account and player schema.",
    };
}
