using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WslCare.Core.Files;

/// <summary>WR-T1 — who owns a path, for the Windows <c>%TEMP%</c> cleanup (PLAN_windows_runner.md §3.12: an entry not owned by the
/// current user's SID is never deleted).</summary>
public sealed partial class PhysicalFileSystem
{
    /// <summary>The owner's SID of the file or folder at <paramref name="path"/>; empty on every other system and when it cannot be
    /// read (the caller then treats the entry as not its own). Asked only of an entry the listing reported as no link.</summary>
    public string OwnerSidOf(string path) => OperatingSystem.IsWindows() ? OwnerOnWindows(path) : string.Empty;

    [SupportedOSPlatform("windows")]
    private static string OwnerOnWindows(string path)
    {
        try
        {
            FileSystemSecurity security = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Owner);
            return security.GetOwner(typeof(SecurityIdentifier))?.Value ?? string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        {
            return string.Empty;
        }
    }
}
