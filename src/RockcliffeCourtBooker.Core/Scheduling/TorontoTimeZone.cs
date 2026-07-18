namespace RockcliffeCourtBooker.Core;

public static class TorontoTimeZone
{
    public const string IanaId = "America/Toronto";
    public const string WindowsId = "Eastern Standard Time";

    public static TimeZoneInfo GetSystemTimeZone()
    {
        foreach (var id in OperatingSystem.IsWindows()
                     ? new[] { WindowsId, IanaId }
                     : new[] { IanaId, WindowsId })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                // Try the platform's alternate identifier.
            }
            catch (InvalidTimeZoneException)
            {
                // Try the platform's alternate identifier.
            }
        }

        throw new TimeZoneNotFoundException("The America/Toronto time zone is not installed on this computer.");
    }
}
