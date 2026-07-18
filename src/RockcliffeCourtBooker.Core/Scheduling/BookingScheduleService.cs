namespace RockcliffeCourtBooker.Core;

public sealed class BookingScheduleService
{
    public static readonly TimeOnly OpeningLocalTime = new(8, 0);
    public static readonly TimeOnly WorkerStartLocalTime = new(7, 58);
    public static readonly TimeOnly UnattendedCutoffLocalTime = new(8, 15);

    private readonly TimeZoneInfo _timeZone;

    public BookingScheduleService(TimeZoneInfo? timeZone = null)
    {
        _timeZone = timeZone ?? TorontoTimeZone.GetSystemTimeZone();
    }

    public TimeZoneInfo TimeZone => _timeZone;

    public BookingWindow GetWindow(DateOnly targetDate)
    {
        var bookingDay = targetDate.AddDays(-3);
        return new BookingWindow(
            targetDate,
            ConvertLocalToUtc(bookingDay, WorkerStartLocalTime),
            ConvertLocalToUtc(bookingDay, OpeningLocalTime),
            ConvertLocalToUtc(bookingDay, UnattendedCutoffLocalTime));
    }

    public DateTimeOffset GetOpeningTimeUtc(DateOnly targetDate) => GetWindow(targetDate).OpensAtUtc;

    public DateTimeOffset GetUnattendedCutoffUtc(DateOnly targetDate) => GetWindow(targetDate).UnattendedCutoffAtUtc;

    public bool IsWithinUnattendedWindow(DateOnly targetDate, DateTimeOffset instantUtc)
    {
        var window = GetWindow(targetDate);
        return instantUtc >= window.WorkerStartsAtUtc && instantUtc < window.UnattendedCutoffAtUtc;
    }

    public bool IsMissedWindow(DateOnly targetDate, DateTimeOffset instantUtc) =>
        instantUtc >= GetWindow(targetDate).UnattendedCutoffAtUtc;

    public DateOnly? GetNextUnopenedTargetDate(BookingRule rule, DateTimeOffset instantUtc)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (!rule.Enabled)
        {
            return null;
        }

        if (rule.Kind == BookingRuleKind.OneTime)
        {
            return rule.TargetDate is { } target && GetOpeningTimeUtc(target) > instantUtc ? target : null;
        }

        var localNow = TimeZoneInfo.ConvertTime(instantUtc, _timeZone);
        var localDate = DateOnly.FromDateTime(localNow.DateTime);

        for (var offset = 0; offset <= 14; offset++)
        {
            var candidate = localDate.AddDays(offset);
            if (rule.Weekdays.Contains(candidate.DayOfWeek) && GetOpeningTimeUtc(candidate) > instantUtc)
            {
                return candidate;
            }
        }

        return null;
    }

    private DateTimeOffset ConvertLocalToUtc(DateOnly date, TimeOnly time)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        if (_timeZone.IsInvalidTime(local))
        {
            throw new InvalidOperationException($"The local time {local:O} does not exist in {_timeZone.Id}.");
        }

        var offset = _timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
