using System.Xml.Linq;
using RockcliffeCourtBooker.Scheduling;
using Xunit;

namespace RockcliffeCourtBooker.Scheduling.Tests;

public sealed class TaskSchedulerXmlBuilderTests
{
    private static readonly string AbsoluteWorkerPath = OperatingSystem.IsWindows()
        ? @"C:\Apps\Rockcliffe\RockcliffeCourtBooker.Worker.exe"
        : "/apps/rockcliffe/RockcliffeCourtBooker.Worker";

    private static readonly string AbsoluteWorkingDirectory = OperatingSystem.IsWindows()
        ? @"C:\Apps\Rockcliffe"
        : "/apps/rockcliffe";

    [Theory]
    [InlineData(DayOfWeek.Monday, DayOfWeek.Friday)]
    [InlineData(DayOfWeek.Tuesday, DayOfWeek.Saturday)]
    [InlineData(DayOfWeek.Wednesday, DayOfWeek.Sunday)]
    [InlineData(DayOfWeek.Sunday, DayOfWeek.Thursday)]
    public void ShiftToOpeningDayUsesThreeCalendarDays(DayOfWeek target, DayOfWeek expected)
    {
        Assert.Equal(expected, TaskSchedulerXmlBuilder.ShiftToOpeningDay(target));
    }

    [Fact]
    public void BuildMapsTargetDateToOpeningDateAtSevenFiftyEight()
    {
        var plan = CreatePlan([new DateOnly(2026, 7, 20)], []);

        var result = new TaskSchedulerXmlBuilder().Build(plan, "S-1-5-21-test", new DateOnly(2026, 7, 17));

        Assert.Equal(1, result.TriggerCount);
        Assert.Contains("2026-07-17T07:58:00", result.Xml, StringComparison.Ordinal);
        Assert.Contains("InteractiveToken", result.Xml, StringComparison.Ordinal);
        Assert.Contains("WakeToRun", result.Xml, StringComparison.Ordinal);
        Assert.Contains("<RunOnlyIfNetworkAvailable>true</RunOnlyIfNetworkAvailable>", result.Xml, StringComparison.Ordinal);
        Assert.Contains("<ExecutionTimeLimit>PT20M</ExecutionTimeLimit>", result.Xml, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCreatesOneWeeklyTriggerForSeveralTargetDays()
    {
        var plan = CreatePlan([], [DayOfWeek.Monday, DayOfWeek.Wednesday]);

        var result = new TaskSchedulerXmlBuilder().Build(plan, "S-1-5-21-test", new DateOnly(2026, 7, 17));
        var document = XDocument.Parse(result.Xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal(1, result.TriggerCount);
        Assert.Single(document.Descendants(ns + "CalendarTrigger"));
        Assert.Single(document.Descendants(ns + "Friday"));
        Assert.Single(document.Descendants(ns + "Sunday"));
    }

    [Fact]
    public void BuildDropsPastOneTimeTriggers()
    {
        var plan = CreatePlan(
            [new DateOnly(2026, 7, 18), new DateOnly(2026, 7, 21)],
            []);

        var result = new TaskSchedulerXmlBuilder().Build(plan, "S-1-5-21-test", new DateOnly(2026, 7, 17));

        Assert.Equal(1, result.TriggerCount);
        Assert.DoesNotContain("2026-07-15", result.Xml, StringComparison.Ordinal);
        Assert.Contains("2026-07-18", result.Xml, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRejectsMoreThanFortyEightTriggers()
    {
        var dates = Enumerable.Range(0, 49)
            .Select(offset => new DateOnly(2026, 8, 1).AddDays(offset))
            .ToArray();
        var plan = CreatePlan(dates, []);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new TaskSchedulerXmlBuilder().Build(plan, "S-1-5-21-test", new DateOnly(2026, 7, 17)));

        Assert.Contains("at most 48", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ClockHealthAcceptsAReportedSuccessfulNetworkSynchronization()
    {
        const string status = """
            Leap Indicator: 0(no warning)
            Last Successful Sync Time: 7/17/2026 7:42:01 AM
            Source: time.windows.com,0x9
            """;

        Assert.True(WindowsSystemHealthService.IsClockSynchronized(status));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Leap Indicator: 3(not synchronized)\nLast Successful Sync Time: unspecified\nSource: Local CMOS Clock")]
    [InlineData("Leap Indicator: 0(no warning)\nLast Successful Sync Time: 7/17/2026 7:42:01 AM\nSource: Local CMOS Clock")]
    [InlineData("Leap Indicator: 0(no warning)\nSource: time.windows.com,0x9")]
    public void ClockHealthFailsClosedForUnhealthyOrIncompleteStatus(string status)
    {
        Assert.False(WindowsSystemHealthService.IsClockSynchronized(status));
    }

    [Fact]
    public void ClockHealthAcceptsLocalizedW32TimeLabels()
    {
        const string status = """
            Indicateur de saut : 0(aucun avertissement)
            Couche : 3
            Précision : -23
            Délai racine : 0.01s
            Dispersion racine : 1.2s
            Identificateur de référence : 0x01020304
            Dernière synchronisation réussie : 17/07/2026 07:42:01
            Source : time.windows.com,0x9
            Intervalle d'interrogation : 10
            """;

        Assert.True(WindowsSystemHealthService.IsClockSynchronized(status, "NTP"));
    }

    [Fact]
    public void ClockHealthRejectsNoSyncConfigurationEvenWithHealthyLookingOutput()
    {
        const string status = """
            Leap Indicator: 0(no warning)
            Last Successful Sync Time: 7/17/2026 7:42:01 AM
            Source: time.windows.com,0x9
            """;

        Assert.False(WindowsSystemHealthService.IsClockSynchronized(status, "NoSync"));
    }

    private static TaskSchedulePlan CreatePlan(
        IReadOnlyCollection<DateOnly> oneTimeDates,
        IReadOnlyCollection<DayOfWeek> recurringDays) =>
        new(AbsoluteWorkerPath, AbsoluteWorkingDirectory, oneTimeDates, recurringDays);
}
