using System.Globalization;
using System.Text.RegularExpressions;

using WslCare.Core.Collectors;
using WslCare.Core.Health;

namespace WslCare.Core.Processes;

/// <summary>Where the Windows system drive is mounted inside the distro, and the device that mount is.</summary>
public sealed record SystemDriveMount(string MountPoint, uint DeviceMajor, uint DeviceMinor);

/// <summary>
/// Where the distro sees the Windows system drive and whether WSL interop can run a Windows program at all — the inputs of
/// the fallback <see cref="ExecutableResolver"/> takes for the closed list <see cref="Programs"/> (the clock probe's
/// <c>powershell.exe</c>, nothing else) when <c>PATH</c> does not name the program. The files themselves are checked by
/// <see cref="SystemDriveFiles"/>.
/// </summary>
/// <remarks>
/// <para>Why it exists (live, 2026-10-04, the first install): systemd gives <c>wsl-care.service</c> the PATH
/// <c>/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/snap/bin</c>, and WSL appends the Windows folders to PATH only
/// for interactive and login sessions. So under the timer <c>powershell.exe</c> was "not found on PATH", <c>clock.drift</c>
/// was unknown and A16 refused — on every machine. Started by its ABSOLUTE path from a transient unit
/// (<c>systemd-run --wait --pipe -p NoNewPrivileges=yes</c>) the same program ran (exit 0).</para>
/// <para><b>The matching rule</b> (<see cref="Mount"/>), over <see cref="MountInfo"/> — mountinfo, not the mounts table,
/// because it carries each mount's ROOT inside its filesystem and its device. A line is the system drive when its root is
/// <c>/</c> (the whole drive, not a bind of a folder of it), its mount point is absolute, and:</para>
/// <list type="bullet">
/// <item><c>9p</c> (WSL 2, observed 2026-10-04 on WSL 2.7.10): <c>aname=drvfs</c> and a <c>path=</c> option naming
/// <c>C:\</c> or <c>C:</c> (a manual <c>mount -t drvfs C: /mnt/c</c> writes the second) — the SOURCE label is not read;</item>
/// <item><c>drvfs</c> (WSL 1, not observed here): the source <c>C:\</c> or <c>C:</c>;</item>
/// <item><c>virtiofs</c> (WSL's <c>virtiofs=true</c> mode, not observed here): the source or a <c>path=</c> option naming
/// the drive. WSL mounts the share by a TAG and bind-mounts a child of it onto the drive's folder, so such a line names
/// neither and is NOT taken — the refusal then names the filesystem found at the automount folder.</item>
/// </list>
/// <para>Fields are decoded from the kernel's octal escapes (<c>\040</c> space, <c>\011</c> tab, <c>\012</c> newline,
/// <c>\134</c> backslash) and options split at both <c>,</c> and <c>;</c>. One mount point is the answer (several lines
/// for it — mounts stacked on one folder — agree, and the last, the one a path reaches, gives the device); several
/// DIFFERENT mount points are refused, naming them: which one is the drive is not guessed. Derived this way,
/// <c>[automount] root=</c> is honoured as WSL applied it, and a folder nobody mounted is never searched (only root and
/// WSL's init can mount; a FUSE mount by a user has a type this rule never takes).</para>
/// <para><b>Which drive Windows is installed on is NOT derived — a documented assumption: <c>C:</c>.</b> Nothing a systemd
/// service can read inside the distro names <c>%SystemDrive%</c>; a machine with Windows elsewhere gets the old answer,
/// with a reason naming the path checked. Searching every mounted drive instead would let a folder a Windows user may
/// create on a DATA drive (<c>D:\Windows\System32\…</c>) be started by the root daemon.</para>
/// <para><b>Interop</b> (<see cref="InteropRefusal"/>): a Windows program runs only through WSL's binfmt_misc handler, so
/// the handler must be registered and <c>enabled</c> (observed 2026-10-04: <c>WSLInterop</c>, interpreter <c>/init</c>,
/// magic <c>4d5a</c> = <c>MZ</c>).</para>
/// </remarks>
public static partial class WindowsSystemDrive
{
    /// <summary>The drive Windows is installed on — assumed, not derived (see the remarks).</summary>
    public const string Drive = @"C:\";

    /// <summary>This process's mounts with each mount's root and device, written by the kernel.</summary>
    public const string MountInfo = "/proc/self/mountinfo";

    /// <summary>WSL's per-distro settings; read only to NAME the automount folder in a refusal.</summary>
    public const string WslConf = "/etc/wsl.conf";

    /// <summary>WSL's interop handler in binfmt_misc: <c>WSLInterop</c> (observed 2026-10-04, WSL 2.7.10) or the
    /// <c>WSLInterop-late</c> newer WSL registers late, after systemd's binfmt service — either answers.</summary>
    public static IReadOnlyList<string> InteropEntries { get; } =
        ["/proc/sys/fs/binfmt_misc/WSLInterop", "/proc/sys/fs/binfmt_misc/WSLInterop-late"];

    /// <summary>How long the whole system-drive lookup may take before it is refused (it reads a 9p share the host serves).</summary>
    public static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The CLOSED list of Windows programs looked up on the system drive when PATH does not name them, each with its folder
    /// relative to the drive's root. A name not here is looked up on PATH alone, exactly as before.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Programs { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Where Windows installs Windows PowerShell 5.1 (%SystemRoot%\System32\WindowsPowerShell\v1.0) — the folder WSL's
        // own interop PATH names for it.
        [HealthCommands.PowerShell] = "Windows/System32/WindowsPowerShell/v1.0",
    };

    private static readonly string[] DriveNames = [Drive, Drive.TrimEnd('\\')];

    /// <summary>The system drive's mount per <paramref name="mountInfo"/> (the text of <see cref="MountInfo"/>), or why
    /// not — none (naming what IS at <paramref name="automountRoot"/><c>c</c>), or several different mount points (named).</summary>
    public static Reading<SystemDriveMount> Mount(string mountInfo, string automountRoot)
    {
        var lines = mountInfo.Split('\n').SelectMany(MountInfoLine.Parse).ToList();
        var drive = lines.Where(IsTheDrive).ToList();
        var points = drive.Select(l => l.MountPoint).Distinct(StringComparer.Ordinal).ToList();
        return points.Count switch
        {
            1 => Reading.Of(drive.Last().ToMount()),
            0 => Reading.Missing<SystemDriveMount>(NoDrive(lines, automountRoot)),
            _ => Reading.Missing<SystemDriveMount>($"{MountInfo} mounts {Drive} at {points.Count} different places ({string.Join(", ", points)}); which one is the drive is not guessed"),
        };
    }

    /// <summary><see cref="Mount"/> over this process's mountinfo, naming the automount folder <see cref="WslConf"/> sets.</summary>
    public static Reading<SystemDriveMount> MountHere()
    {
        var automount = ReadText(WslConf).Map(HealthParsers.AutomountRoot).ValueOr("/mnt/");
        return ReadText(MountInfo).Bind(text => Mount(text, automount));
    }

    /// <summary>Empty when WSL interop is registered and enabled per <paramref name="read"/> (one of <see cref="InteropEntries"/>
    /// whose first line is <c>enabled</c>); otherwise why a Windows program cannot run from here.</summary>
    public static string InteropRefusal(Func<string, Reading<string>> read)
    {
        var readings = InteropEntries.Select(e => (Entry: e, Text: read(e))).ToList();
        var registered = readings.Where(r => r.Text.IsAvailable).ToList();
        return registered.Count == 0
            ? $"WSL interop is not registered ({string.Join("; ", readings.Select(r => r.Text.ReasonOrEmpty))}), so no Windows program can run from this distro"
            : EnabledOrWhy(registered[0].Entry, registered[0].Text.ValueOr(string.Empty));
    }

    /// <summary><see cref="InteropRefusal"/> over this machine's binfmt_misc.</summary>
    public static string InteropRefusalHere() => InteropRefusal(ReadText);

    private static string EnabledOrWhy(string entry, string text)
    {
        var first = text.Split('\n')[0].Trim();
        return first == "enabled"
            ? string.Empty
            : $"WSL interop is registered but not enabled ({entry} says \"{first}\"), so no Windows program can run from this distro";
    }

    private static bool IsTheDrive(MountInfoLine line) => IsAWholeDriveAtAnAbsolutePoint(line) && IsADrvfsMountOfTheDrive(line);

    /// <summary>The whole drive (root <c>/</c>, not a bound folder of it), mounted at an absolute path.</summary>
    private static bool IsAWholeDriveAtAnAbsolutePoint(MountInfoLine line) => line.Root == "/" && IsAbsolute(line.MountPoint);

    private static bool IsADrvfsMountOfTheDrive(MountInfoLine line) => IsWsl2Drvfs(line) || IsWsl1Drvfs(line) || IsVirtiofs(line);

    /// <summary>WSL 2: <c>9p</c> with <c>aname=drvfs</c>, judged by its <c>path=</c> option — never by the source label.</summary>
    private static bool IsWsl2Drvfs(MountInfoLine line) =>
        line.Type == "9p" && line.Options.Contains("aname=drvfs", StringComparer.Ordinal) && NamesTheDrive(PathOption(line));

    /// <summary>WSL 1: type <c>drvfs</c>, judged by its source.</summary>
    private static bool IsWsl1Drvfs(MountInfoLine line) => line.Type == "drvfs" && NamesTheDrive(line.Source);

    /// <summary>virtiofs, only when the source or a <c>path=</c> option names the drive (WSL's tag-mounted share never does).</summary>
    private static bool IsVirtiofs(MountInfoLine line) =>
        line.Type == "virtiofs" && (NamesTheDrive(line.Source) || NamesTheDrive(PathOption(line)));

    private static bool NamesTheDrive(string name) => DriveNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static string PathOption(MountInfoLine line) =>
        line.Options.Where(o => o.StartsWith("path=", StringComparison.Ordinal)).Select(o => o["path=".Length..]).FirstOrDefault() ?? string.Empty;

    /// <summary>Why no line is the drive — and what is at the automount folder instead, when something is.</summary>
    private static string NoDrive(IReadOnlyList<MountInfoLine> lines, string automountRoot)
    {
        var folder = automountRoot.TrimEnd('/') + "/c";
        var there = lines.LastOrDefault(l => l.MountPoint == folder);
        var found = there is null
            ? $"nothing is mounted at {folder}"
            : $"{folder} is a {there.Type} mount of {there.Source} (root {there.Root}), which this rule does not identify as {Drive}";
        return $"{MountInfo} has no drvfs mount of {Drive} as a whole drive at an absolute path; {found}";
    }

    /// <summary>The kernel's mount points start at <c>/</c>; the rest of the clause is this host's own rule, the same on Linux
    /// and what lets the Windows test leg point a table at its temporary folder.</summary>
    private static bool IsAbsolute(string mountPoint) => mountPoint.StartsWith('/') || Path.IsPathFullyQualified(mountPoint);

    private static string Unescaped(string field) =>
        OctalEscape().Replace(field, m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString());

    private static Reading<string> ReadText(string path)
    {
        try
        {
            return Reading.Of(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Reading.Missing<string>(e is FileNotFoundException or DirectoryNotFoundException ? $"{path} does not exist" : $"{path} could not be read: {e.Message}");
        }
    }

    [GeneratedRegex(@"\\([0-7]{3})")]
    private static partial Regex OctalEscape();

    /// <summary>One mountinfo line: <c>id parent major:minor root mount-point options [optional…] - type source super-options</c>.</summary>
    private sealed record MountInfoLine(string Root, string MountPoint, uint Major, uint Minor, string Type, string Source, IReadOnlyList<string> Options)
    {
        /// <summary>The line, or nothing when it is not one (empty, or short of its fields).</summary>
        public static IEnumerable<MountInfoLine> Parse(string line)
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var dash = f.Length >= 10 ? Array.IndexOf(f, "-", 6) : -1;
            return dash > 0 && dash + 3 < f.Length && Device(f[2]) is [var major, var minor]
                ? [new(Unescaped(f[3]), Unescaped(f[4]), major, minor, f[dash + 1], Unescaped(f[dash + 2]), Unescaped(f[dash + 3]).Split([',', ';']))]
                : [];
        }

        public SystemDriveMount ToMount() => new(MountPoint, Major, Minor);

        /// <summary><c>major:minor</c> as two numbers; empty when it is not that.</summary>
        private static uint[] Device(string field)
        {
            var parts = field.Split(':');
            return parts.Length == 2 && uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) && uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
                ? [major, minor]
                : [];
        }
    }
}
