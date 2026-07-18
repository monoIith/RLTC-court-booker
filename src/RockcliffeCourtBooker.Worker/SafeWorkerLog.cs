using RockcliffeCourtBooker.Automation.Security;

namespace RockcliffeCourtBooker.Worker;

internal sealed class SafeWorkerLog
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private readonly string _directory;
    private readonly SensitiveDataRedactor _redactor;

    public SafeWorkerLog(string directory, SensitiveDataRedactor redactor)
    {
        _directory = directory;
        _redactor = redactor;
    }

    public void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var line = $"{DateTimeOffset.UtcNow:O} {_redactor.Redact(message)}{Environment.NewLine}";
            File.AppendAllText(
                Path.Combine(_directory, $"worker-{DateTimeOffset.UtcNow:yyyyMMdd}.log"),
                line);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Logging must never change the booking outcome.
        }
    }

    public void DeleteExpired()
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        var cutoff = DateTimeOffset.UtcNow.Subtract(Retention).UtcDateTime;
        foreach (var file in Directory.EnumerateFiles(_directory, "worker-*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Retention is best-effort.
            }
        }
    }
}
