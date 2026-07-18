using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace RockcliffeCourtBooker.Scheduling;

public sealed record WindowsSystemHealth(
    bool IsWindows,
    bool IsEasternTimeZone,
    bool ClockIsSynchronized,
    string TimeZoneId,
    string ClockStatus)
{
    public bool CanScheduleUnattended => IsWindows && IsEasternTimeZone && ClockIsSynchronized;
}

public sealed class WindowsSystemHealthService
{
    private readonly string _requiredTimeZoneId;

    public WindowsSystemHealthService(string requiredTimeZoneId = "Eastern Standard Time")
    {
        _requiredTimeZoneId = requiredTimeZoneId;
    }

    [SupportedOSPlatform("windows")]
    public async Task<WindowsSystemHealth> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WindowsSystemHealth(false, false, false, TimeZoneInfo.Local.Id, "Not running on Windows.");
        }

        var timeZoneId = TimeZoneInfo.Local.Id;
        var clockStatus = await QueryClockStatusAsync(cancellationToken).ConfigureAwait(false);
        var timeServiceType = Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\W32Time\Parameters",
            "Type",
            null) as string;
        return new WindowsSystemHealth(
            true,
            string.Equals(timeZoneId, _requiredTimeZoneId, StringComparison.Ordinal),
            IsClockSynchronized(clockStatus, timeServiceType),
            timeZoneId,
            clockStatus);
    }

    internal static bool IsClockSynchronized(string output, string? timeServiceType = "NTP")
    {
        if (string.IsNullOrWhiteSpace(output) ||
            string.Equals(timeServiceType, "NoSync", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("unspecified", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("not synchronized", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Local CMOS Clock", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Free-running System Clock", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Preserve the clear English fast path while supporting localized Windows.
        if (output.Contains("Leap Indicator: 0", StringComparison.OrdinalIgnoreCase) &&
            output.Contains("Last Successful Sync Time:", StringComparison.OrdinalIgnoreCase) &&
            output.Contains("Source:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // w32tm localizes labels and descriptions, but its status field order and
        // numeric leap-indicator value are stable. Parse only the value after the
        // first colon on each non-empty line; colons inside the localized timestamp
        // therefore do not affect field positions.
        var values = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static line => line.IndexOf(':', StringComparison.Ordinal) is var separator && separator >= 0
                ? line[(separator + 1)..].Trim()
                : string.Empty)
            .ToArray();
        if (values.Length < 8)
        {
            return false;
        }

        var leapText = values[0].Split('(', 2, StringSplitOptions.TrimEntries)[0];
        return int.TryParse(leapText, out var leapIndicator) &&
               leapIndicator == 0 &&
               !string.IsNullOrWhiteSpace(values[6]) &&
               !string.IsNullOrWhiteSpace(values[7]);
    }

    [SupportedOSPlatform("windows")]
    private static async Task<string> QueryClockStatusAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "w32tm.exe"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("/query");
        startInfo.ArgumentList.Add("/status");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows Time Service status could not be queried.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        return process.ExitCode == 0 ? output : error;
    }
}
