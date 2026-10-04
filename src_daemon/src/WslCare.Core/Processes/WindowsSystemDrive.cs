using System.Text.RegularExpressions;

using WslCare.Core.Collectors;
using WslCare.Core.Health;

namespace WslCare.Core.Processes;

/// <summary>
/// Where the distro sees the Windows system drive, and the Windows programs the product starts from it when <c>PATH</c>
/// does not name them — the clock probe's <c>powershell.exe</c> and nothing else (<see cref="Programs"/>).
/// </summary>
/// <remarks>
/// <para>Why it exists (live, 2026-10-04, the first install): systemd gives <c>wsl-care.service</c> the PATH
/// <c>/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/snap/bin</c>, and WSL appends the Windows folders to PATH only
/// for interactive and login sessions. So under the timer <c>powershell.exe</c> was "not found on PATH", <c>clock.drift</c>
/// was unknown and A16 refused — on every machine. Started by its ABSOLUTE path from a transient unit
/// (<c>systemd-run --wait --pipe -p NoNewPrivileges=yes</c>) the same program ran (exit 0): WSL interop works under the
/// service, only the lookup did not.</para>
/// <para><b>Where the drive is mounted is DERIVED</b>, from the kernel's mount table (<see cref="MountTable"/>): the
/// <c>drvfs</c> mount whose source is the drive root <see cref="Drive"/>. That is where WSL put it, so an
/// <c>[automount] root=</c> in <c>/etc/wsl.conf</c> is honoured without reading <c>wsl.conf</c> — and a folder nobody
/// mounted (say, a planted <c>/mnt/c</c> under a root pointed somewhere writable) is never searched: only root and WSL's
/// init can add a mount. Docker Desktop's mounts of a FOLDER of <c>C:</c> and a mapped network drive are not the drive root
/// and never match.</para>
/// <para><b>Which drive Windows is installed on is NOT derived, and that is a documented assumption:</b> <c>C:</c>. Nothing
/// a systemd service can read inside the distro names the system drive (<c>%SystemDrive%</c> is a Windows variable,
/// and WSL passes the Windows environment only to interactive sessions). A machine with Windows on another drive gets
/// the old answer — not found, with a reason naming the path checked — and its owner can still put the folder on the
/// unit's PATH with a drop-in. Searching every mounted drive instead would let a folder a Windows user may create on a
/// DATA drive (<c>D:\Windows\System32\…</c>) be started by the root daemon; <c>C:\Windows</c> is the one tree Windows
/// itself protects.</para>
/// </remarks>
public static partial class WindowsSystemDrive
{
    /// <summary>The drive Windows is installed on — assumed, not derived (see the remarks).</summary>
    public const string Drive = @"C:\";

    /// <summary>This process's view of the mounts — the same table <c>mount</c> prints, written by the kernel.</summary>
    public const string MountTable = "/proc/self/mounts";

    /// <summary>
    /// The CLOSED list of Windows programs looked up on the system drive when PATH does not name them, each with its folder
    /// relative to the drive's root. A name not here is looked up on PATH alone, exactly as before.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Programs { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Where Windows installs Windows PowerShell 5.1 (%SystemRoot%\System32\WindowsPowerShell\v1.0) on every edition
        // the distro runs on — the folder WSL's own interop PATH names for it.
        [HealthCommands.PowerShell] = "Windows/System32/WindowsPowerShell/v1.0",
    };

    /// <summary>Where <see cref="Drive"/> is mounted, per <paramref name="mountTable"/> (the text of <see cref="MountTable"/>),
    /// or why it is not: the first absolute mount point of a <c>drvfs</c> mount whose source is the drive root.</summary>
    public static Reading<string> MountPoint(string mountTable) =>
        mountTable.Split('\n').Select(MountPointOfTheDrive).FirstOrDefault(p => p.Length > 0) is { } found
            ? Reading.Of(found)
            : Reading.Missing<string>($"{MountTable} has no drvfs mount of {Drive} at an absolute path");

    /// <summary><see cref="MountPoint"/> over this process's own mount table; why not, when it cannot be read.</summary>
    public static Reading<string> MountPointHere()
    {
        try
        {
            return MountPoint(File.ReadAllText(MountTable));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Reading.Missing<string>($"{MountTable} could not be read: {e.Message}");
        }
    }

    /// <summary>The line's mount point when the line mounts <see cref="Drive"/> through drvfs; empty otherwise.</summary>
    private static string MountPointOfTheDrive(string line)
    {
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 4 && IsADriveRootMount(fields) ? Unescaped(fields[1]) : string.Empty;
    }

    /// <summary>Source, mount point, type and options of a mount table line describe <see cref="Drive"/> through drvfs, at an
    /// absolute mount point.</summary>
    private static bool IsADriveRootMount(string[] fields) =>
        string.Equals(Unescaped(fields[0]), Drive, StringComparison.OrdinalIgnoreCase) && IsDrvfs(fields[2], fields[3]) && IsAbsolute(Unescaped(fields[1]));

    /// <summary>The kernel's mount points start at <c>/</c>; the rest of the clause is this host's own rule, the same on Linux and
    /// what lets the Windows test leg point a table at its temporary folder.</summary>
    private static bool IsAbsolute(string mountPoint) => mountPoint.StartsWith('/') || Path.IsPathFullyQualified(mountPoint);

    /// <summary>WSL 2 mounts a drive as <c>9p</c> with <c>aname=drvfs</c> (observed 2026-10-04); a <c>drvfs</c> type is the
    /// same filesystem where the kernel does not translate it (WSL 1 — not observed here).</summary>
    private static bool IsDrvfs(string type, string options) =>
        type == "drvfs" || (type == "9p" && options.Split([',', ';']).Contains("aname=drvfs", StringComparer.Ordinal));

    /// <summary>The kernel writes a space, a tab, a newline and a backslash in a mount table field as three octal digits.</summary>
    private static string Unescaped(string field) =>
        OctalEscape().Replace(field, m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString());

    [GeneratedRegex(@"\\([0-7]{3})")]
    private static partial Regex OctalEscape();
}
