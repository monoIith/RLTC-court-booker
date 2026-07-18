using System.Xml.Linq;

namespace RockcliffeCourtBooker.Scheduling;

public sealed class TaskSchedulerXmlBuilder
{
    private static readonly XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private readonly TimeOnly _startTime;

    public TaskSchedulerXmlBuilder(TimeOnly? startTime = null)
    {
        _startTime = startTime ?? new TimeOnly(7, 58);
    }

    public TaskScheduleBuildResult Build(TaskSchedulePlan plan, string userId, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        if (!Path.IsPathFullyQualified(plan.WorkerExecutable))
        {
            throw new ArgumentException("The worker executable path must be absolute.", nameof(plan));
        }

        if (!Path.IsPathFullyQualified(plan.WorkingDirectory))
        {
            throw new ArgumentException("The working directory path must be absolute.", nameof(plan));
        }

        var oneTimeOpeningDates = plan.OneTimeTargetDates
            .Distinct()
            .Select(static targetDate => targetDate.AddDays(-3))
            .Where(openingDate => openingDate >= today)
            .Order()
            .ToArray();

        var recurringTriggerDays = plan.RecurringTargetDays
            .Distinct()
            .Select(ShiftToOpeningDay)
            .OrderBy(static day => (int)day)
            .ToArray();

        var triggerCount = oneTimeOpeningDates.Length + (recurringTriggerDays.Length > 0 ? 1 : 0);
        if (triggerCount > TaskSchedulePlan.MaximumTaskSchedulerTriggers)
        {
            throw new InvalidOperationException(
                $"The schedule requires {triggerCount} triggers; Windows Task Scheduler permits at most {TaskSchedulePlan.MaximumTaskSchedulerTriggers}.");
        }

        var triggers = new XElement(TaskNamespace + "Triggers");
        foreach (var openingDate in oneTimeOpeningDates)
        {
            triggers.Add(new XElement(
                TaskNamespace + "TimeTrigger",
                new XElement(TaskNamespace + "StartBoundary", FormatBoundary(openingDate)),
                new XElement(TaskNamespace + "Enabled", "true")));
        }

        if (recurringTriggerDays.Length > 0)
        {
            var firstTriggerDate = FindNextDay(today, recurringTriggerDays);
            var daysOfWeek = new XElement(TaskNamespace + "DaysOfWeek");
            foreach (var day in recurringTriggerDays)
            {
                daysOfWeek.Add(new XElement(TaskNamespace + day.ToString()));
            }

            triggers.Add(new XElement(
                TaskNamespace + "CalendarTrigger",
                new XElement(TaskNamespace + "StartBoundary", FormatBoundary(firstTriggerDate)),
                new XElement(TaskNamespace + "Enabled", "true"),
                new XElement(
                    TaskNamespace + "ScheduleByWeek",
                    new XElement(TaskNamespace + "WeeksInterval", "1"),
                    daysOfWeek)));
        }

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(
                TaskNamespace + "Task",
                new XAttribute("version", "1.4"),
                new XElement(
                    TaskNamespace + "RegistrationInfo",
                    new XElement(TaskNamespace + "Description", "Runs authorized Rockcliffe clay-court booking requests."),
                    new XElement(TaskNamespace + "URI", "\\RockcliffeCourtBooker")),
                triggers,
                new XElement(
                    TaskNamespace + "Principals",
                    new XElement(
                        TaskNamespace + "Principal",
                        new XAttribute("id", "Author"),
                        new XElement(TaskNamespace + "UserId", userId),
                        new XElement(TaskNamespace + "LogonType", "InteractiveToken"),
                        new XElement(TaskNamespace + "RunLevel", "LeastPrivilege"))),
                new XElement(
                    TaskNamespace + "Settings",
                    new XElement(TaskNamespace + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(TaskNamespace + "DisallowStartIfOnBatteries", "false"),
                    new XElement(TaskNamespace + "StopIfGoingOnBatteries", "false"),
                    new XElement(TaskNamespace + "AllowHardTerminate", "true"),
                    new XElement(TaskNamespace + "StartWhenAvailable", "true"),
                    // If the PC wakes before Wi-Fi reconnects, let Task Scheduler defer
                    // the launch. The worker still enforces the hard 8:15 AM cutoff.
                    new XElement(TaskNamespace + "RunOnlyIfNetworkAvailable", "true"),
                    new XElement(TaskNamespace + "IdleSettings",
                        new XElement(TaskNamespace + "StopOnIdleEnd", "false"),
                        new XElement(TaskNamespace + "RestartOnIdle", "false")),
                    new XElement(TaskNamespace + "AllowStartOnDemand", "true"),
                    new XElement(TaskNamespace + "Enabled", "true"),
                    new XElement(TaskNamespace + "Hidden", "false"),
                    new XElement(TaskNamespace + "RunOnlyIfIdle", "false"),
                    new XElement(TaskNamespace + "WakeToRun", "true"),
                    new XElement(TaskNamespace + "ExecutionTimeLimit", "PT20M"),
                    new XElement(TaskNamespace + "Priority", "5")),
                new XElement(
                    TaskNamespace + "Actions",
                    new XAttribute("Context", "Author"),
                    new XElement(
                        TaskNamespace + "Exec",
                        new XElement(TaskNamespace + "Command", plan.WorkerExecutable),
                        new XElement(TaskNamespace + "Arguments", plan.WorkerArguments),
                        new XElement(TaskNamespace + "WorkingDirectory", plan.WorkingDirectory)))));

        return new TaskScheduleBuildResult(document.ToString(), triggerCount);
    }

    public static DayOfWeek ShiftToOpeningDay(DayOfWeek targetDay) =>
        (DayOfWeek)(((int)targetDay - 3 + 7) % 7);

    private static DateOnly FindNextDay(DateOnly today, IReadOnlyCollection<DayOfWeek> allowedDays)
    {
        for (var offset = 0; offset < 7; offset++)
        {
            var candidate = today.AddDays(offset);
            if (allowedDays.Contains(candidate.DayOfWeek))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("At least one recurring trigger day is required.");
    }

    private string FormatBoundary(DateOnly date) =>
        date.ToDateTime(_startTime).ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
}
