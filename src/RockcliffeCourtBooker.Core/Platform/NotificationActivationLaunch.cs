namespace RockcliffeCourtBooker.Core;

public static class NotificationActivationLaunch
{
    public const string Sentinel = "----AppNotificationActivated:";

    public static bool IsActivationLaunch(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Count == 1 &&
               string.Equals(arguments[0], Sentinel, StringComparison.Ordinal);
    }
}
