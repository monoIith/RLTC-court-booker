using System.Text.Json;
using RockcliffeCourtBooker.Core;

namespace RockcliffeCourtBooker.Infrastructure;

public sealed class ExternalConfigurationLoader : IExternalConfigurationLoader
{
    public const int SupportedSchemaVersion = 1;
    public const long MaximumFileSizeBytes = 1024 * 1024;

    private readonly ConfigurationFileSecurityInspector _securityInspector;
    private readonly string _managedDataDirectory;

    public ExternalConfigurationLoader()
        : this(new ConfigurationFileSecurityInspector(), AppPaths.CreateDefault().DataDirectory)
    {
    }

    public ExternalConfigurationLoader(AppPaths appPaths)
        : this(
            new ConfigurationFileSecurityInspector(),
            (appPaths ?? throw new ArgumentNullException(nameof(appPaths))).DataDirectory)
    {
    }

    internal ExternalConfigurationLoader(ConfigurationFileSecurityInspector securityInspector)
        : this(securityInspector, AppPaths.CreateDefault().DataDirectory)
    {
    }

    private ExternalConfigurationLoader(
        ConfigurationFileSecurityInspector securityInspector,
        string managedDataDirectory)
    {
        _securityInspector = securityInspector;
        _managedDataDirectory = Path.GetFullPath(managedDataDirectory);
    }

    public async Task<ExternalConfigurationLoadResult> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ExternalConfigurationException("path.required", "An absolute configuration file path is required.");
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw new ExternalConfigurationException("path.absolute", "The configuration file path must be absolute.");
        }

        var fullPath = Path.GetFullPath(path);
        if (IsWithinDirectory(fullPath, _managedDataDirectory))
        {
            throw new ExternalConfigurationException(
                "path.managed",
                "The external configuration file must be stored outside the application's managed data directory.");
        }

        FileInfo file;
        try
        {
            file = new FileInfo(fullPath);
            if (!file.Exists)
            {
                throw new ExternalConfigurationException("file.missing", "The external configuration file does not exist.");
            }

            if (file.Length == 0 || file.Length > MaximumFileSizeBytes)
            {
                throw new ExternalConfigurationException("file.size", "The external configuration file must be between 1 byte and 1 MiB.");
            }
        }
        catch (ExternalConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ExternalConfigurationException("file.unreadable", "The external configuration file could not be inspected.", exception);
        }

        JsonDocument document;
        try
        {
            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            document = await JsonDocument.ParseAsync(
                stream,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new ExternalConfigurationException("json.invalid", "The external configuration file is not valid JSON.", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ExternalConfigurationException("file.unreadable", "The external configuration file could not be read.", exception);
        }

        using (document)
        {
            var configuration = Parse(document.RootElement);
            var warnings = _securityInspector.Inspect(fullPath);
            return new ExternalConfigurationLoadResult(fullPath, configuration, warnings);
        }
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative) &&
               !string.Equals(relative, "..", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static ExternalConfiguration Parse(JsonElement root)
    {
        RequireObject(root, "root");
        RejectDuplicateProperties(root, "root");

        var schemaVersionElement = RequireProperty(root, "schemaVersion");
        if (schemaVersionElement.ValueKind != JsonValueKind.Number ||
            !schemaVersionElement.TryGetInt32(out var schemaVersion))
        {
            throw Invalid("schemaVersion.type", "schemaVersion must be an integer.");
        }

        if (schemaVersion != SupportedSchemaVersion)
        {
            throw Invalid("schemaVersion.unsupported", $"Only schemaVersion {SupportedSchemaVersion} is supported.");
        }

        var accountElement = RequireProperty(root, "account");
        RequireObject(accountElement, "account");
        RejectDuplicateProperties(accountElement, "account");

        var account = new ExternalAccount
        {
            MemberName = RequireTrimmedString(accountElement, "memberName", "account.memberName"),
            Username = RequireTrimmedString(accountElement, "username", "account.username"),
            Password = RequirePassword(accountElement),
        };

        var playersElement = RequireProperty(root, "players");
        if (playersElement.ValueKind != JsonValueKind.Array || playersElement.GetArrayLength() == 0)
        {
            throw Invalid("players.required", "players must be a non-empty array.");
        }

        var players = new List<ExternalPlayer>(playersElement.GetArrayLength());
        foreach (var (playerElement, index) in playersElement.EnumerateArray().Select(static (element, index) => (element, index)))
        {
            RequireObject(playerElement, $"players[{index}]");
            RejectDuplicateProperties(playerElement, $"players[{index}]");
            players.Add(new ExternalPlayer(
                RequireTrimmedString(playerElement, "id", $"players[{index}].id"),
                RequireTrimmedString(playerElement, "displayName", $"players[{index}].displayName")));
        }

        if (players.Select(static player => player.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != players.Count)
        {
            throw Invalid("players.idDuplicate", "Player IDs must be unique, ignoring case.");
        }

        if (players.Select(static player => player.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != players.Count)
        {
            throw Invalid("players.nameDuplicate", "Player display names must be unique, ignoring case.");
        }

        return new ExternalConfiguration
        {
            SchemaVersion = schemaVersion,
            Account = account,
            Players = players,
        };
    }

    private static JsonElement RequireProperty(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            throw Invalid("property.missing", $"The required property '{name}' is missing.");
        }

        return value;
    }

    private static string RequireTrimmedString(JsonElement parent, string name, string path)
    {
        var value = RequireString(parent, name, path);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw Invalid("string.whitespace", $"The property '{path}' cannot contain leading or trailing whitespace.");
        }

        return value;
    }

    private static string RequirePassword(JsonElement account)
    {
        var password = RequireString(account, "password", "account.password");
        if (string.IsNullOrWhiteSpace(password))
        {
            throw Invalid("password.required", "account.password cannot be empty or whitespace.");
        }

        return password;
    }

    private static string RequireString(JsonElement parent, string name, string path)
    {
        var element = RequireProperty(parent, name);
        if (element.ValueKind != JsonValueKind.String)
        {
            throw Invalid("string.type", $"The property '{path}' must be a string.");
        }

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid("string.required", $"The property '{path}' cannot be empty or whitespace.");
        }

        return value;
    }

    private static void RequireObject(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("object.type", $"The property '{path}' must be an object.");
        }
    }

    private static void RejectDuplicateProperties(JsonElement element, string path)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw Invalid("property.duplicate", $"The object '{path}' contains the duplicate property '{property.Name}'.");
            }
        }
    }

    private static ExternalConfigurationException Invalid(string code, string message) => new(code, message);
}
