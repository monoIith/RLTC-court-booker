using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace RockcliffeCourtBooker.Notifications;

public sealed class WindowsBookingNotificationService : IBookingNotificationService
{
    private static readonly object ProcessRegistrationGate = new();
    private static bool processRegistered;
    private static int processRegistrationOwners;

    private readonly object instanceGate = new();
    private Action<Guid>? activated;
    private bool handlerAttached;
    private bool ownsProcessRegistration;
    private bool disposed;

    public static bool IsSupported() =>
        OperatingSystem.IsWindows() && AppNotificationManager.IsSupported();

    public void Register(Action<Guid>? activated = null)
    {
        EnsureSupported();
        var attachedNow = false;
        var acquireRegistration = false;
        lock (instanceGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (activated is not null)
            {
                // Repeated registration on the same service replaces the callback instead of
                // attaching another manager event handler.
                this.activated = activated;
                if (!handlerAttached)
                {
                    AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
                    handlerAttached = true;
                    attachedNow = true;
                }
            }

            if (!ownsProcessRegistration)
            {
                ownsProcessRegistration = true;
                acquireRegistration = true;
            }
        }

        // Register can synchronously deliver a pending activation. Do not hold the
        // instance lock while calling it because the event handler also takes that lock.
        try
        {
            if (acquireRegistration)
            {
                AcquireProcessRegistration();
            }
        }
        catch
        {
            lock (instanceGate)
            {
                if (acquireRegistration)
                {
                    ownsProcessRegistration = false;
                }

                if (attachedNow && handlerAttached)
                {
                    AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
                    handlerAttached = false;
                    this.activated = null;
                }
            }

            throw;
        }
    }

    public void Show(BookingNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        EnsureSupported();
        Register();

        var appNotification = new AppNotificationBuilder()
            .AddArgument("attemptId", notification.AttemptId.ToString("D"))
            .AddArgument("outcome", notification.Succeeded ? "succeeded" : "failed")
            .AddText(notification.Title)
            .AddText(notification.Message)
            .BuildNotification();

        AppNotificationManager.Default.Show(appNotification);
    }

    public void Dispose()
    {
        var releaseRegistration = false;
        lock (instanceGate)
        {
            if (disposed)
            {
                return;
            }

            if (handlerAttached && OperatingSystem.IsWindows())
            {
                AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
                handlerAttached = false;
            }

            activated = null;
            releaseRegistration = ownsProcessRegistration;
            ownsProcessRegistration = false;
            disposed = true;
        }

        if (releaseRegistration && OperatingSystem.IsWindows())
        {
            ReleaseProcessRegistration();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Permanently removes all app-notification registration data during uninstall.
    /// Normal service disposal calls Unregister instead so an existing notification
    /// can launch a fresh activation process when clicked later.
    /// </summary>
    public static void UnregisterAll()
    {
        EnsureWindows();
        if (!AppNotificationManager.IsSupported())
        {
            return;
        }

        lock (ProcessRegistrationGate)
        {
            try
            {
                // Permanent uninstall cleanup removes all notification registration data.
                AppNotificationManager.Default.UnregisterAll();
            }
            finally
            {
                processRegistered = false;
                processRegistrationOwners = 0;
            }
        }
    }

    private static void AcquireProcessRegistration()
    {
        lock (ProcessRegistrationGate)
        {
            if (!processRegistered)
            {
                AppNotificationManager.Default.Register();
                processRegistered = true;
            }

            processRegistrationOwners++;
        }
    }

    private static void ReleaseProcessRegistration()
    {
        lock (ProcessRegistrationGate)
        {
            if (processRegistrationOwners > 0)
            {
                processRegistrationOwners--;
            }

            if (processRegistrationOwners == 0 && processRegistered)
            {
                // Microsoft requires normal processes to unregister before exit;
                // clicking an existing toast will then launch a fresh worker process.
                AppNotificationManager.Default.Unregister();
                processRegistered = false;
            }
        }
    }

    private void OnNotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args)
    {
        _ = sender;
        var values = ParseArguments(args.Argument);
        if (values.TryGetValue("attemptId", out var value) && Guid.TryParse(value, out var attemptId))
        {
            Action<Guid>? callback;
            lock (instanceGate)
            {
                callback = disposed ? null : activated;
            }

            callback?.Invoke(attemptId);
        }
    }

    internal static IReadOnlyDictionary<string, string> ParseArguments(string arguments)
    {
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in arguments.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..separator]);
            var value = Uri.UnescapeDataString(pair[(separator + 1)..]);
            parsed[key] = value;
        }

        return parsed;
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows app notifications are available only on Windows.");
        }
    }

    private static void EnsureSupported()
    {
        EnsureWindows();
        if (!AppNotificationManager.IsSupported())
        {
            throw new PlatformNotSupportedException(
                "Windows app notifications are not supported on this installation.");
        }
    }
}
