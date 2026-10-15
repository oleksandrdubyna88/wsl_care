using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WslCare.Core.Processes;

/// <summary>
/// The real <see cref="IAdminOnlyPath"/>: reads the owner and the access entries of the file or folder (the security descriptor — the
/// same managed call <see cref="Archive.WindowsAccess"/> makes, AOT-safe) and hands them to <see cref="AdminOnlyJudge"/>. A descriptor
/// that cannot be read is a problem, never a pass: what cannot be inspected is not known to be administrators' alone.
/// </summary>
public sealed class WindowsAdminOnlyPath : IAdminOnlyPath
{
    private const AclRight AllRights = AclRight.WriteData | AclRight.AppendData | AclRight.DeleteSubdirectoriesAndFiles | AclRight.Delete | AclRight.ChangePermissions | AclRight.TakeOwnership;

    public string Problem(string path, AdminOnlyRole role) =>
        OperatingSystem.IsWindows() ? ProblemOnWindows(path, role) : "the owner and permissions of a file can be judged on Windows only";

    [SupportedOSPlatform("windows")]
    private static string ProblemOnWindows(string path, AdminOnlyRole role)
    {
        try
        {
            return AdminOnlyJudge.Problem(Read(path, role), role);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException or InvalidOperationException or ArgumentException)
        {
            return $"its owner and permissions could not be read ({e.Message})";
        }
    }

    [SupportedOSPlatform("windows")]
    private static AclFacts Read(string path, AdminOnlyRole role)
    {
        FileSystemSecurity security = role == AdminOnlyRole.ProgramFile ? new FileInfo(path).GetAccessControl() : new DirectoryInfo(path).GetAccessControl();
        var owner = security.GetOwner(typeof(SecurityIdentifier))?.Value ?? string.Empty;
        var entries = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Select(Entry);
        return new AclFacts(owner, [.. entries]);
    }

    [SupportedOSPlatform("windows")]
    private static AclEntry Entry(FileSystemAccessRule rule) => new(
        rule.IdentityReference.Value,
        rule.AccessControlType == AccessControlType.Allow,
        (AclRight)((int)rule.FileSystemRights & (int)AllRights),
        rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly));
}
