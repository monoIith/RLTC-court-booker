namespace RockcliffeCourtBooker.Scheduling;

public sealed record TaskSchedulePlan(
    string WorkerExecutable,
    string WorkingDirectory,
    IReadOnlyCollection<DateOnly> OneTimeTargetDates,
    IReadOnlyCollection<DayOfWeek> RecurringTargetDays,
    string WorkerArguments = "run-due --scheduled")
{
    public const int MaximumTaskSchedulerTriggers = 48;
}

public sealed record TaskScheduleBuildResult(string Xml, int TriggerCount);
