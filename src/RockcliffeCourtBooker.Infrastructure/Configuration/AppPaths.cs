namespace RockcliffeCourtBooker.Infrastructure;

public sealed record AppPaths
{
    public const string ApplicationDirectoryName = "RockcliffeCourtBooker";

    public AppPaths(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory);
        DatabasePath = Path.Combine(DataDirectory, "court-booker.db");
        LogsDirectory = Path.Combine(DataDirectory, "logs");
        DiagnosticsDirectory = Path.Combine(DataDirectory, "diagnostics");
    }

    public string DataDirectory { get; }

    public string DatabasePath { get; }

    public string LogsDirectory { get; }

    public string DiagnosticsDirectory { get; }

    public static AppPaths CreateDefault()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("The local application data directory is unavailable.");
        }

        return new AppPaths(Path.Combine(localApplicationData, ApplicationDirectoryName));
    }

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(DiagnosticsDirectory);
    }
}
