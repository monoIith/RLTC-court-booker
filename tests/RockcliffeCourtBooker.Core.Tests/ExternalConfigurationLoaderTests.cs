using RockcliffeCourtBooker.Infrastructure;

namespace RockcliffeCourtBooker.Core.Tests;

public sealed class ExternalConfigurationLoaderTests
{
    private const string ValidJson = """
        {
          "schemaVersion": 1,
          "account": {
            "memberName": "Account Holder",
            "username": "member@example.com",
            "password": "never-log-this"
          },
          "players": [
            { "id": "player-one", "displayName": "Player One" },
            { "id": "player-two", "displayName": "Player Two" }
          ],
          "futureField": true
        }
        """;

    [Fact]
    public async Task LoadAsync_LoadsValidFileAndToleratesExtraFields()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.GetPath("account.json");
        await File.WriteAllTextAsync(path, ValidJson);

        var result = await new ExternalConfigurationLoader().LoadAsync(path);

        Assert.Equal(System.IO.Path.GetFullPath(path), result.SourcePath);
        Assert.Equal(1, result.Configuration.SchemaVersion);
        Assert.Equal("Account Holder", result.Configuration.Account.MemberName);
        Assert.Equal("member@example.com", result.Configuration.Account.Username);
        Assert.Equal("never-log-this", result.Configuration.Account.Password);
        Assert.Equal(["player-one", "player-two"], result.Configuration.Players.Select(static player => player.Id));
        Assert.DoesNotContain("never-log-this", result.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_RereadsChangedOriginalFile()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.GetPath("account.json");
        await File.WriteAllTextAsync(path, ValidJson);
        var loader = new ExternalConfigurationLoader();

        var first = await loader.LoadAsync(path);
        await File.WriteAllTextAsync(path, ValidJson.Replace("Player One", "Updated Player", StringComparison.Ordinal));
        var second = await loader.LoadAsync(path);

        Assert.Equal("Player One", first.Configuration.Players[0].DisplayName);
        Assert.Equal("Updated Player", second.Configuration.Players[0].DisplayName);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"account\":{},\"players\":[]}", "schemaVersion.unsupported")]
    [InlineData("{\"schemaVersion\":1,\"account\":{},\"players\":[]}", "property.missing")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"account\":{},\"players\":[]}", "property.duplicate")]
    [InlineData("{\"schemaVersion\":1,\"account\":{\"memberName\":\"A\",\"username\":\"U\",\"password\":\"P\"},\"players\":[{\"id\":\"x\",\"displayName\":\"X\"},{\"id\":\"X\",\"displayName\":\"Y\"}]}", "players.idDuplicate")]
    [InlineData("{\"schemaVersion\":1,\"account\":{\"memberName\":\"A\",\"username\":\"U\",\"password\":\"P\"},\"players\":[{\"id\":\"x\",\"displayName\":\"Same\"},{\"id\":\"y\",\"displayName\":\"same\"}]}", "players.nameDuplicate")]
    [InlineData("{\"schemaVersion\":1,\"account\":{\"memberName\":\" A\",\"username\":\"U\",\"password\":\"P\"},\"players\":[{\"id\":\"x\",\"displayName\":\"X\"}]}", "string.whitespace")]
    public async Task LoadAsync_RejectsInvalidContract(string json, string expectedCode)
    {
        using var directory = new TemporaryDirectory();
        var path = directory.GetPath("account.json");
        await File.WriteAllTextAsync(path, json);

        var exception = await Assert.ThrowsAsync<ExternalConfigurationException>(
            () => new ExternalConfigurationLoader().LoadAsync(path));

        Assert.Equal(expectedCode, exception.Code);
        Assert.DoesNotContain("P\"", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_RejectsRelativeAndMissingPaths()
    {
        var loader = new ExternalConfigurationLoader();

        var relative = await Assert.ThrowsAsync<ExternalConfigurationException>(() => loader.LoadAsync("account.json"));
        var missing = await Assert.ThrowsAsync<ExternalConfigurationException>(
            () => loader.LoadAsync(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".json")));

        Assert.Equal("path.absolute", relative.Code);
        Assert.Equal("file.missing", missing.Code);
    }

    [Fact]
    public async Task LoadAsync_RejectsConfigurationInsideManagedApplicationData()
    {
        using var directory = new TemporaryDirectory();
        var managedDirectory = directory.GetPath("managed-app-data");
        Directory.CreateDirectory(managedDirectory);
        var path = System.IO.Path.Combine(managedDirectory, "account.json");
        await File.WriteAllTextAsync(path, ValidJson);

        var exception = await Assert.ThrowsAsync<ExternalConfigurationException>(
            () => new ExternalConfigurationLoader(new AppPaths(managedDirectory)).LoadAsync(path));

        Assert.Equal("path.managed", exception.Code);
    }

    [Fact]
    public async Task LoadAsync_WarnsForBroadUnixReadPermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var path = directory.GetPath("account.json");
        await File.WriteAllTextAsync(path, ValidJson);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        var result = await new ExternalConfigurationLoader().LoadAsync(path);

        Assert.Single(result.SecurityWarnings);
        Assert.Contains("other users", result.SecurityWarnings[0], StringComparison.OrdinalIgnoreCase);
    }
}
