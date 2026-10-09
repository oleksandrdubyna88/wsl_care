using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WslCare.Core.Archive;

/// <summary>
/// Who besides its owner may read a Windows folder (plan §15r D7, review M6): the folder's access rules for Everyone, Users and
/// Authenticated Users. A warning, never a refusal — a share's own permissions are the share's, and a person decides.
/// </summary>
public static class WindowsAccess
{
    [SupportedOSPlatform("windows")]
    private static readonly (WellKnownSidType Sid, string Name)[] Broad =
    [
        (WellKnownSidType.WorldSid, "Everyone"),
        (WellKnownSidType.BuiltinUsersSid, "Users"),
        (WellKnownSidType.AuthenticatedUserSid, "Authenticated Users"),
    ];

    [SupportedOSPlatform("windows")]
    private const FileSystemRights Reading = FileSystemRights.ReadData | FileSystemRights.ListDirectory;

    /// <summary>One sentence per broad group allowed to read <paramref name="onDisk"/>; empty on another OS or when the rules
    /// cannot be read (then one sentence saying so).</summary>
    public static IReadOnlyList<string> Warnings(string onDisk, string folder) =>
        OperatingSystem.IsWindows() ? Read(onDisk, folder) : [];

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> Read(string onDisk, string folder)
    {
        try
        {
            var rules = new DirectoryInfo(onDisk).GetAccessControl().GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
            var readers = rules.OfType<FileSystemAccessRule>()
                .Where(r => r.AccessControlType == AccessControlType.Allow && (r.FileSystemRights & Reading) != 0)
                .SelectMany(r => Broad.Where(b => r.IdentityReference is SecurityIdentifier sid && sid.IsWellKnown(b.Sid)).Select(b => b.Name))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return readers.Count == 0 ? [] : [$"{string.Join(", ", readers)} may read {folder}: archived sessions hold what the agents saw — remove those groups from the folder's permissions"];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException or InvalidOperationException)
        {
            return [$"who may read {folder} could not be read ({e.Message})"];
        }
    }
}
