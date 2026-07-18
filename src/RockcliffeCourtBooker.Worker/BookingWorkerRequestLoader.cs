using System.Text.Json;
using RockcliffeCourtBooker.Worker.Contracts;

namespace RockcliffeCourtBooker.Worker;

internal static class BookingWorkerRequestLoader
{
    private const long MaximumRequestBytes = 1024 * 1024;

    public static async Task<BookingWorkerRequest> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new WorkerInputException("--request must specify an absolute JSON file path.");
        }

        var file = new FileInfo(path);
        if (!file.Exists || file.Length is <= 0 or > MaximumRequestBytes)
        {
            throw new WorkerInputException("The request file must exist and be between 1 byte and 1 MiB.");
        }

        try
        {
            await using var stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var request = await JsonSerializer.DeserializeAsync<BookingWorkerRequest>(
                stream,
                WorkerJson.Options,
                cancellationToken);
            return request ?? throw new WorkerInputException("The request JSON is empty.");
        }
        catch (JsonException exception)
        {
            throw new WorkerInputException("The request file is not valid worker JSON.", exception);
        }
        catch (IOException exception)
        {
            throw new WorkerInputException("The request file could not be read.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new WorkerInputException("The request file could not be read.", exception);
        }
    }
}

internal sealed class WorkerInputException : Exception
{
    public WorkerInputException(string message)
        : base(message)
    {
    }

    public WorkerInputException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
