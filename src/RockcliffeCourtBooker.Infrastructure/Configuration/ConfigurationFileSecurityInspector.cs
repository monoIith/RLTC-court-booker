using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Diagnostics.CodeAnalysis;

namespace RockcliffeCourtBooker.Infrastructure;

internal sealed class ConfigurationFileSecurityInspector
{
    private const string BroadReadWarning =
        "The external configuration file appears readable by other users. Restrict its permissions because it contains a plaintext password.";

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "The inspector has an instance API so it can be replaced in tests and future platforms.")]
    public IReadOnlyList<string> Inspect(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return IsBroadlyReadableOnWindows(path) ? [BroadReadWarning] : [];
            }

            var mode = File.GetUnixFileMode(path);
            const UnixFileMode broadRead = UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            return (mode & broadRead) != 0 ? [BroadReadWarning] : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return ["The application could not verify the external configuration file's access permissions."];
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsBroadlyReadableOnWindows(string path)
    {
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        var broadSids = new HashSet<string>(StringComparer.Ordinal)
        {
            new SecurityIdentifier(WellKnownSidType.WorldSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value,
        };

        const FileSystemRights readRights =
            FileSystemRights.Read | FileSystemRights.ReadData | FileSystemRights.ReadAndExecute | FileSystemRights.FullControl;

        return rules
            .OfType<FileSystemAccessRule>()
            .Any(rule =>
                rule.AccessControlType == AccessControlType.Allow &&
                (rule.FileSystemRights & readRights) != 0 &&
                rule.IdentityReference is SecurityIdentifier sid &&
                broadSids.Contains(sid.Value));
    }
}
