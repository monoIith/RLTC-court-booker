using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RockcliffeCourtBooker.App.Services;

internal enum WorkerBookingKind
{
    Singles,
    Doubles,
}

internal enum WorkerAutomationMode
{
    ReadOnly,
    DryRun,
    Submit,
}

internal enum WorkerOutcome
{
    Completed,
    InvalidRequest,
    ConfigurationFailed,
    MissedWindow,
    AlreadyRunning,
    AutomationFailed,
}

internal enum WorkerAutomationStatus
{
    Succeeded,
    ReadOnlyComplete,
    DryRunComplete,
    NoAvailability,
    ValidationFailed,
    AuthenticationFailed,
    PlayerMismatch,
    NonZeroPrice,
    CaptchaOrMfaRequired,
    PageStructureChanged,
    BookingRejected,
    SubmissionUncertain,
    Cancelled,
    UnexpectedError,
}

internal sealed record WorkerRequest
{
    public int SchemaVersion { get; init; } = 1;

    public required Guid AttemptId { get; init; }

    public required Guid RuleId { get; init; }

    public required string AccountConfigurationPath { get; init; }

    public required DateOnly TargetDate { get; init; }

    public required IReadOnlyList<TimeOnly> OrderedStartTimes { get; init; }

    public required WorkerBookingKind BookingKind { get; init; }

    public required int DurationMinutes { get; init; }

    public required IReadOnlyList<string> PlayerIds { get; init; }

    public required IReadOnlyList<int> CourtOrder { get; init; }

    public required WorkerAutomationMode Mode { get; init; }

    public bool TermsAuthorized { get; init; }

    public bool VisibleBrowser { get; init; }

    public bool UserInitiated { get; init; }

    public string? DiagnosticsDirectory { get; init; }

    public string? ChromiumExecutablePath { get; init; }
}

internal sealed record WorkerAutomationResult
{
    public required string AttemptId { get; init; }

    public required WorkerAutomationStatus Status { get; init; }

    public required string Message { get; init; }

    public int? CourtNumber { get; init; }

    public TimeOnly? StartTime { get; init; }

    public string? ScreenshotPath { get; init; }

    public string? TracePath { get; init; }

    public DateTimeOffset CompletedAtUtc { get; init; }
}

internal sealed record WorkerResult
{
    public required Guid AttemptId { get; init; }

    public required WorkerOutcome Outcome { get; init; }

    public required string Message { get; init; }

    public WorkerAutomationResult? AutomationResult { get; init; }

    public DateTimeOffset CompletedAtUtc { get; init; }
}

internal sealed record WorkerProcessResult(
    bool ProcessStarted,
    int? ExitCode,
    WorkerResult? Result,
    string FailureMessage);

internal sealed class WorkerProcessClient(string workerExecutable, string requestDirectory)
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(21);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string workerExecutable = Path.GetFullPath(workerExecutable);
    private readonly string requestDirectory = Path.GetFullPath(requestDirectory);

    public async Task<WorkerProcessResult> ExecuteAsync(
        WorkerRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(workerExecutable))
        {
            return new WorkerProcessResult(
                false,
                null,
                null,
                "RockcliffeCourtBooker.Worker.exe is missing beside the app; no booking was attempted.");
        }

        Directory.CreateDirectory(requestDirectory);
        var requestPath = Path.Combine(requestDirectory, $"{request.AttemptId:D}.request.json");
        var resultPath = Path.Combine(requestDirectory, $"{request.AttemptId:D}.result.json");
        var processStarted = false;
        try
        {
            await File.WriteAllTextAsync(
                requestPath,
                JsonSerializer.Serialize(request, JsonOptions),
                cancellationToken);

            var startInfo = new ProcessStartInfo
            {
                FileName = workerExecutable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(workerExecutable)!,
            };
            startInfo.ArgumentList.Add("--request");
            startInfo.ArgumentList.Add(requestPath);
            startInfo.ArgumentList.Add("--result");
            startInfo.ArgumentList.Add(resultPath);

            using var process = new Process { StartInfo = startInfo };
            try
            {
                process.Start();
                processStarted = true;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return new WorkerProcessResult(false, null, null, "Windows could not start the booking worker.");
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProcessTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return new WorkerProcessResult(
                    true,
                    null,
                    null,
                    "The worker exceeded its maximum runtime; the submission result is uncertain.");
            }

            var output = await standardOutput;
            _ = await standardError;
            var result = await ReadResultAsync(resultPath, output, cancellationToken);
            return result is null
                ? new WorkerProcessResult(
                    true,
                    process.ExitCode,
                    null,
                    "The worker did not return a valid result; the submission result is uncertain.")
                : new WorkerProcessResult(true, process.ExitCode, result, string.Empty);
        }
        catch (JsonException)
        {
            return new WorkerProcessResult(
                true,
                null,
                null,
                "The worker returned an unreadable result; the submission result is uncertain.");
        }
        catch (IOException)
        {
            return new WorkerProcessResult(
                processStarted,
                null,
                null,
                processStarted
                    ? "The app could not read the worker result; the submission result is uncertain."
                    : "The app could not create the local worker request files; no booking was attempted.");
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(resultPath);
        }
    }

    private static async Task<WorkerResult?> ReadResultAsync(
        string resultPath,
        string standardOutput,
        CancellationToken cancellationToken)
    {
        var json = File.Exists(resultPath)
            ? await File.ReadAllTextAsync(resultPath, cancellationToken)
            : standardOutput;
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<WorkerResult>(json, JsonOptions);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = false,
            PropertyNameCaseInsensitive = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Request files contain no credentials and are cleaned on the next app run if a process still holds them.
        }
    }
}
