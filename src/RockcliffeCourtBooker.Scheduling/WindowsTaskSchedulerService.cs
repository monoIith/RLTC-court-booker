using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

namespace RockcliffeCourtBooker.Scheduling;

public sealed record TaskSchedulerCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

public sealed class TaskSchedulerCommandException(string message, TaskSchedulerCommandResult result)
    : InvalidOperationException(message)
{
    public TaskSchedulerCommandResult Result { get; } = result;
}

public sealed class WindowsTaskSchedulerService
{
    public const string TaskName = "RockcliffeCourtBooker";
    private readonly TaskSchedulerXmlBuilder _xmlBuilder;
    private readonly string _taskName;

    public WindowsTaskSchedulerService(TaskSchedulerXmlBuilder xmlBuilder, string taskName = TaskName)
    {
        _xmlBuilder = xmlBuilder;
        _taskName = taskName;
    }

    [SupportedOSPlatform("windows")]
    public async Task RebuildAsync(TaskSchedulePlan plan, DateOnly today, CancellationToken cancellationToken = default)
    {
        EnsureWindows();

        var triggerCount = plan.OneTimeTargetDates
            .Distinct()
            .Count(target => target.AddDays(-3) >= today)
            + (plan.RecurringTargetDays.Count > 0 ? 1 : 0);

        if (triggerCount == 0)
        {
            await DeleteAsync(ignoreMissing: true, cancellationToken).ConfigureAwait(false);
            return;
        }

        var userId = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current Windows user SID could not be determined.");
        var build = _xmlBuilder.Build(plan, userId, today);
        var temporaryFile = Path.Combine(Path.GetTempPath(), $"RockcliffeCourtBooker-{Guid.NewGuid():N}.xml");

        try
        {
            await File.WriteAllTextAsync(temporaryFile, build.Xml, Encoding.Unicode, cancellationToken).ConfigureAwait(false);
            var result = await RunAsync(["/Create", "/TN", _taskName, "/XML", temporaryFile, "/F"], cancellationToken)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                throw new TaskSchedulerCommandException("Windows Task Scheduler rejected the generated booking task.", result);
            }
        }
        finally
        {
            File.Delete(temporaryFile);
        }
    }

    [SupportedOSPlatform("windows")]
    public async Task DeleteAsync(bool ignoreMissing = false, CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        var result = await RunAsync(["/Delete", "/TN", _taskName, "/F"], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded && !(ignoreMissing && IsMissingTask(result)))
        {
            throw new TaskSchedulerCommandException("The Rockcliffe booking task could not be removed.", result);
        }
    }

    [SupportedOSPlatform("windows")]
    public async Task<TaskSchedulerCommandResult> QueryAsync(CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        return await RunAsync(["/Query", "/TN", _taskName, "/FO", "LIST", "/V"], cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsMissingTask(TaskSchedulerCommandResult result) =>
        result.StandardError.Contains("cannot find", StringComparison.OrdinalIgnoreCase)
        || result.StandardOutput.Contains("cannot find", StringComparison.OrdinalIgnoreCase);

    [SupportedOSPlatform("windows")]
    private static async Task<TaskSchedulerCommandResult> RunAsync(
        IReadOnlyCollection<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("Windows Task Scheduler could not be started.", exception);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new TaskSchedulerCommandResult(
            process.ExitCode,
            await outputTask.ConfigureAwait(false),
            await errorTask.ConfigureAwait(false));
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Task Scheduler is available only on Windows.");
        }
    }
}
