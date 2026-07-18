using RockcliffeCourtBooker.App.Models;
using RockcliffeCourtBooker.Core;
using RockcliffeCourtBooker.Scheduling;

namespace RockcliffeCourtBooker.App.Services;

internal sealed class WindowsSchedulerCoordinator
{
    private readonly IBookingRepository repository;
    private readonly BookingScheduleService scheduleService;
    private readonly WindowsTaskSchedulerService taskScheduler;
    private readonly WindowsSystemHealthService systemHealth;
    private readonly string workerExecutable;
    private readonly string workingDirectory;

    public WindowsSchedulerCoordinator(IBookingRepository repository, string workerExecutable)
    {
        this.repository = repository;
        this.workerExecutable = Path.GetFullPath(workerExecutable);
        workingDirectory = Path.GetDirectoryName(this.workerExecutable)
            ?? throw new ArgumentException("The worker executable must have a parent directory.", nameof(workerExecutable));
        scheduleService = new BookingScheduleService();
        taskScheduler = new WindowsTaskSchedulerService(new TaskSchedulerXmlBuilder());
        systemHealth = new WindowsSystemHealthService();
    }

    public async Task<OperationResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        var settings = await repository.GetSettingsAsync(cancellationToken);
        if (!settings.UnattendedSubmissionEnabled)
        {
            await DeleteTaskSafelyAsync(cancellationToken);
            return OperationResult.Success("Unattended booking is disabled.");
        }

        if (!File.Exists(workerExecutable))
        {
            return OperationResult.Failure(
                "The booking worker executable is missing beside the app. Unattended booking was not scheduled.");
        }

        WindowsSystemHealth health;
        try
        {
            health = await systemHealth.CheckAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return OperationResult.Failure(
                "Windows clock health could not be verified. Unattended booking was not scheduled.");
        }

        if (!health.CanScheduleUnattended)
        {
            return OperationResult.Failure(BuildSystemHealthDetail(health));
        }

        var rules = await repository.GetRulesAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var localNow = TimeZoneInfo.ConvertTime(now, scheduleService.TimeZone);
        var localToday = DateOnly.FromDateTime(localNow.DateTime);
        var schedulerStartDate = localNow.TimeOfDay < BookingScheduleService.OpeningLocalTime.ToTimeSpan()
            ? localToday
            : localToday.AddDays(1);
        var activeRules = rules.Where(static rule => rule.Enabled).ToArray();
        var plan = new TaskSchedulePlan(
            workerExecutable,
            workingDirectory,
            activeRules
                .Where(static rule => rule.Kind == BookingRuleKind.OneTime && rule.TargetDate is not null)
                .Select(static rule => rule.TargetDate!.Value)
                .Where(date => scheduleService.GetOpeningTimeUtc(date) > now)
                .ToArray(),
            activeRules
                .Where(static rule => rule.Kind == BookingRuleKind.Weekly)
                .SelectMany(static rule => rule.Weekdays)
                .Distinct()
                .ToArray());

        try
        {
            await taskScheduler.RebuildAsync(plan, schedulerStartDate, cancellationToken);
            return OperationResult.Success("Windows Task Scheduler is synchronized.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            return OperationResult.Failure(
                "Windows Task Scheduler could not be synchronized. Unattended booking must remain disabled until this is fixed.");
        }
    }

    public async Task<SchedulerHealthSnapshot> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var settings = await repository.GetSettingsAsync(cancellationToken);
        var rules = await repository.GetRulesAsync(cancellationToken);
        var nextRun = CalculateNextRun(rules, DateTimeOffset.UtcNow);
        if (!settings.UnattendedSubmissionEnabled)
        {
            return new SchedulerHealthSnapshot(
                true,
                "Unattended booking disabled",
                null,
                "Saved rules will not run automatically. Use Setup to enable unattended submission or use Book now.");
        }

        if (!File.Exists(workerExecutable))
        {
            return new SchedulerHealthSnapshot(
                false,
                "Worker missing",
                nextRun,
                "RockcliffeCourtBooker.Worker.exe was not found beside the app.");
        }

        WindowsSystemHealth system;
        try
        {
            system = await systemHealth.CheckAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return new SchedulerHealthSnapshot(
                false,
                "Windows health unavailable",
                nextRun,
                "The app could not verify the Windows clock and time-zone state.");
        }

        if (!system.CanScheduleUnattended)
        {
            return new SchedulerHealthSnapshot(false, "Windows setup needs attention", nextRun, BuildSystemHealthDetail(system));
        }

        if (nextRun is null)
        {
            return new SchedulerHealthSnapshot(
                true,
                "No upcoming rules",
                null,
                "Unattended submission is enabled, but no enabled rule currently has an unopened occurrence.");
        }

        TaskSchedulerCommandResult query;
        try
        {
            query = await taskScheduler.QueryAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return new SchedulerHealthSnapshot(
                false,
                "Scheduler unavailable",
                nextRun,
                "Windows Task Scheduler could not be queried.");
        }

        return query.Succeeded
            ? new SchedulerHealthSnapshot(
                true,
                "Scheduler ready",
                nextRun,
                "The one-time Windows task is installed for the current signed-in user and wakes the computer when due.")
            : new SchedulerHealthSnapshot(
                false,
                "Scheduled task missing",
                nextRun,
                "An upcoming rule exists, but Windows Task Scheduler could not find the RockcliffeCourtBooker task.");
    }

    public DateTimeOffset? CalculateNextRun(IReadOnlyList<BookingRule> rules, DateTimeOffset nowUtc)
    {
        return rules
            .Select(rule => scheduleService.GetNextUnopenedTargetDate(rule, nowUtc))
            .Where(static target => target is not null)
            .Select(target => scheduleService.GetWindow(target!.Value).WorkerStartsAtUtc)
            .Order()
            .Select(static instant => (DateTimeOffset?)instant)
            .FirstOrDefault();
    }

    private async Task DeleteTaskSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await taskScheduler.DeleteAsync(ignoreMissing: true, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            // A stale task is harmless because the worker checks the persisted global toggle before doing any work.
        }
    }

    private static string BuildSystemHealthDetail(WindowsSystemHealth health)
    {
        if (!health.IsEasternTimeZone)
        {
            return $"Windows must use the Eastern Standard Time zone; the current zone is '{health.TimeZoneId}'.";
        }

        if (!health.ClockIsSynchronized)
        {
            return "Windows time synchronization is not healthy. Synchronize the clock before enabling unattended booking.";
        }

        return "Unattended scheduling is available only on Windows.";
    }
}
