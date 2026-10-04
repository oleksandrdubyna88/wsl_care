using System.Globalization;
using System.Runtime.Versioning;

using WslCare.Core.Collectors;
using WslCare.Core.Files;

namespace WslCare.Core.Processes;

/// <summary>
/// What is CHECKED before a Windows program found on the mounted system drive is started (<see cref="WindowsSystemDrive"/>)
/// — and nothing more is claimed for it.
/// </summary>
/// <remarks>
/// <para><b>Above the mount point</b> (<see cref="MountPointRefusal"/>): every ancestor, from <c>/</c> down, is a directory
/// (never a link) owned by root and writable by neither its group nor others — otherwise its owner could swap the tree
/// between the check and the start (an <c>[automount] root=</c> under somebody's home would be that case).</para>
/// <para><b>Below it</b> (<see cref="Problem"/>): no component from the mount point down to the file is a symbolic link, and
/// each lives on the mount's own device; the file is opened ONCE (<c>O_NONBLOCK</c>, so a FIFO or a device never blocks the
/// open) and from that descriptor it is a regular file, the same file (device and inode) the path check saw, on the mount's
/// device, with an execute bit — exec(2)'s own requirement, NOT a protection: drvfs reports a mode of its own (0555 on the
/// whole path, observed 2026-10-04) — and its first two bytes are <c>MZ</c>, the magic WSL's interop handler is registered
/// for. At most two bytes are read.</para>
/// <para><b>What it rests on, unchecked by this code:</b> Windows lets only administrators change <c>C:\Windows\System32</c>.
/// <b>Residuals, recorded rather than closed:</b> an administrator on Windows can replace the file — outside this
/// confused-deputy boundary, since such a person is already above the daemon; another <c>MZ</c> handler in binfmt_misc
/// (wine, mono) could claim the file before interop; and the file is checked by descriptor but STARTED by path (.NET starts
/// no descriptor), so a swap in the milliseconds between is not excluded — on a tree only an administrator can write, whose
/// ancestors only root can.</para>
/// <para>Linux only: the Windows binary never consults the system drive, and on Windows each check answers why not.</para>
/// </remarks>
public static class SystemDriveFiles
{
    private const string NotLinux = "the Windows system drive is inspected only inside a Linux distro";

    private const int GroupOrOtherWrite = 0x12; // 0o022

    private const int AnyExecute = 0x49; // 0o111

    /// <summary>Why the ancestors of <paramref name="mountPoint"/> could let somebody but root change what is under it; empty
    /// when every one is a root-owned directory nobody else may write.</summary>
    public static string MountPointRefusal(string mountPoint) =>
        OperatingSystem.IsLinux() ? AncestorsProblem(mountPoint) : NotLinux;

    /// <summary>Why <paramref name="name"/> in <paramref name="folder"/> on <paramref name="mount"/> is not started, or empty
    /// when every check in the remarks holds.</summary>
    public static string Problem(SystemDriveMount mount, string folder, string name) =>
        OperatingSystem.IsLinux() ? FileProblem(mount, [.. folder.Split('/'), name]) : NotLinux;

    /// <summary><c>/</c>, then each folder above <paramref name="mountPoint"/> — never the mount point itself, which is the
    /// drive's own root and carries the drive's own mode.</summary>
    private static IEnumerable<string> Ancestors(string mountPoint)
    {
        var parts = mountPoint.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return [.. Enumerable.Range(0, parts.Length).Select(n => "/" + string.Join('/', parts[..n]))];
    }

    [SupportedOSPlatform("linux")]
    private static string AncestorsProblem(string mountPoint)
    {
        foreach (var ancestor in Ancestors(mountPoint))
        {
            var problem = AncestorProblem(ancestor);
            if (problem.Length > 0)
            {
                return problem;
            }
        }

        return string.Empty;
    }

    [SupportedOSPlatform("linux")]
    private static string AncestorProblem(string path) => RegularFiles.StatNoFollow(path) switch
    {
        Reading<FileStatus>.Available { Value.IsDirectory: false } => $"{path}, above the drive's mount point, is not a directory (a link or a file)",
        Reading<FileStatus>.Available { Value.OwnerUid: not 0 and var uid } => string.Create(CultureInfo.InvariantCulture, $"{path}, above the drive's mount point, is owned by uid {uid}, not root"),
        Reading<FileStatus>.Available { Value.Permissions: var mode } when (mode & GroupOrOtherWrite) != 0 => $"{path}, above the drive's mount point, is writable by its group or by others (mode {Convert.ToString(mode, 8)})",
        var reading => reading.ReasonOrEmpty,
    };

    [SupportedOSPlatform("linux")]
    private static string FileProblem(SystemDriveMount mount, IReadOnlyList<string> components)
    {
        var path = mount.MountPoint;
        foreach (var component in components)
        {
            path = Path.Combine(path, component);
            var problem = ComponentProblem(path, RegularFiles.StatNoFollow(path), mount);
            if (problem.Length > 0)
            {
                return problem;
            }
        }

        return HeadProblem(path, RegularFiles.StatNoFollow(path), RegularFiles.ReadHead(path, 2), mount);
    }

    private static string ComponentProblem(string path, Reading<FileStatus> status, SystemDriveMount mount) => status switch
    {
        Reading<FileStatus>.Available { Value.IsSymbolicLink: true } => $"{path} is a symbolic link, which is never followed there",
        Reading<FileStatus>.Available { Value: var s } when !OnTheMount(s, mount) => OffTheMount(path, s, mount),
        var reading => reading.ReasonOrEmpty,
    };

    private static string HeadProblem(string path, Reading<FileStatus> checkedPath, Reading<FileHead> head, SystemDriveMount mount) => head switch
    {
        Reading<FileHead>.Available { Value: var opened } => OpenedFileProblem(path, checkedPath, opened, mount),
        var reading => reading.ReasonOrEmpty,
    };

    /// <summary>Identity first (is it the file the path check saw, on the mount), then content.</summary>
    private static string OpenedFileProblem(string path, Reading<FileStatus> checkedPath, FileHead opened, SystemDriveMount mount) =>
        HeadIdentityProblem(path, checkedPath, opened, mount) is { Length: > 0 } identity ? identity : HeadContentProblem(path, opened);

    /// <summary>The opened file is the one the path check described (device and inode), and lives on the drive's mount.</summary>
    private static string HeadIdentityProblem(string path, Reading<FileStatus> checkedPath, FileHead opened, SystemDriveMount mount) =>
        !SameFile(checkedPath, opened.Status) ? $"{path} changed between its check and its open"
        : !OnTheMount(opened.Status, mount) ? OffTheMount(path, opened.Status, mount)
        : string.Empty;

    /// <summary>The opened file carries an execute bit (exec(2)'s requirement) and starts with the <c>MZ</c> magic.</summary>
    private static string HeadContentProblem(string path, FileHead opened) =>
        (opened.Status.Permissions & AnyExecute) == 0 ? $"{path} has no execute bit (exec(2) would refuse it)"
        : !opened.Bytes.SequenceEqual("MZ"u8.ToArray()) ? $"{path} is not a Windows program (no MZ header); only WSL interop may start a file found there"
        : string.Empty;

    private static bool SameFile(Reading<FileStatus> checkedPath, FileStatus opened) =>
        checkedPath is Reading<FileStatus>.Available { Value: var seen } && seen.SameFileAs(opened);

    private static bool OnTheMount(FileStatus status, SystemDriveMount mount) =>
        status.DeviceMajor == mount.DeviceMajor && status.DeviceMinor == mount.DeviceMinor;

    private static string OffTheMount(string path, FileStatus status, SystemDriveMount mount) =>
        string.Create(CultureInfo.InvariantCulture, $"{path} is on device {status.DeviceMajor}:{status.DeviceMinor}, not on the drive's mount ({mount.DeviceMajor}:{mount.DeviceMinor})");
}
