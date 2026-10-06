using System.Runtime.Versioning;

using WslCare.Core.Agents;
using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Archive;

/// <summary>The filesystem the base lies on, as the side judging it found it.</summary>
/// <param name="MountPoint">Where it is mounted (a distro path), the drive's root or the share on Windows.</param>
/// <param name="Type">The filesystem type (<c>ext4</c>, <c>9p</c>, <c>tmpfs</c>) or, on Windows, the drive's kind and format
/// (<c>network</c>, <c>fixed NTFS</c>).</param>
/// <param name="Source">What is mounted there (a device, a drive, a share).</param>
public sealed record BaseMountReport(string MountPoint, string Type, string Source);

/// <summary>
/// What <c>archive check-base</c> answers (plan §15r D7, E9.S0): the base as THIS side sees it, accepted or refused by one named
/// rule, the filesystem it lies on, what a person should know (who else may read it, a coarse time) and nothing else.
/// </summary>
/// <param name="Side"><c>wsl</c> or <c>windows</c>.</param>
/// <param name="Given">The path as it was asked about.</param>
/// <param name="Folder">The base as this side spells it — a Windows drive path asked of the distro is answered with the Linux path
/// it is mounted at; empty when it could not be placed.</param>
/// <param name="Rule">The rule that refused it (<see cref="BaseFolderRule"/>); empty when accepted.</param>
/// <param name="Refusal">The sentence; empty when accepted.</param>
/// <param name="Mount">The filesystem; <c>null</c> when it was not established (a refusal before it, or an unreadable table).</param>
/// <param name="Warnings">Accepted, but a person should act: other accounts may read it, it lives on the distro's own disk.</param>
/// <param name="Notes">Facts that change no decision: a 2-second time granularity, drvfs's reported modes.</param>
public sealed record BaseFolderReport(
    int SchemaVersion,
    string Side,
    string Given,
    bool Accepted,
    string Folder,
    string Rule,
    string Refusal,
    BaseMountReport? Mount,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Notes);

/// <summary>The names <see cref="BaseFolderReport.Rule"/> takes — a closed set, so a client can key on it.</summary>
public static class BaseFolderRule
{
    public const string Shape = "shape";
    public const string DriveNotMounted = "drive-not-mounted";
    public const string MountUnreadable = "mount-unreadable";
    public const string Missing = "missing";
    public const string NotAFolder = "not-a-folder";
    public const string LinkOnTheWay = "link-on-the-way";
    public const string TooBroad = "too-broad";
    public const string Overlap = "overlap";
    public const string Volatile = "volatile-filesystem";
    public const string NotWritable = "not-writable";

    public static IReadOnlyList<string> All { get; } = [Shape, DriveNotMounted, MountUnreadable, Missing, NotAFolder, LinkOnTheWay, TooBroad, Overlap, Volatile, NotWritable];
}

/// <summary>
/// Plan §15r D7 — where the archive may live, judged by the process that will write there (the TARGET USER's, never root's, D1),
/// at <c>config set archive.baseFolder</c>, by <c>archive check-base</c> and again at the start of every run: never trusted
/// because it held once. In order: the shape; on the distro a Windows drive path is placed at its drvfs mount; an EXISTING folder,
/// never created; no link on any component (its real path is its spelling); not a filesystem, drive or share root, not the home;
/// not equal to, inside or containing an agent's folder, the repositories folder, Claude's temporary folders, a folder a cleanup
/// cleans, the product's own folders, the temporary folder; not a filesystem that is gone after a shutdown; writable by this
/// process. Accepted, it may still carry warnings — other accounts may read it (archived sessions hold what the agents saw), it
/// lives on the distro's own disk — and notes.
/// </summary>
/// <remarks>The judge reads: the kernel's mount table (a System read), names and attributes; it creates only the probe file a
/// write test needs, inside the folder, deleted on close.</remarks>
public static class BaseFolderRules
{
    /// <summary>Filesystems a shutdown empties or that hold no files of their own.</summary>
    private static readonly string[] VolatileTypes = ["tmpfs", "ramfs", "proc", "sysfs", "devtmpfs", "overlay", "devpts", "cgroup", "cgroup2"];

    /// <summary>Filesystems that keep a last write to two seconds (FAT) — the index's recorded time is the authority (§15r G6).</summary>
    private static readonly string[] CoarseTimeTypes = ["vfat", "msdos", "exfat", "FAT", "FAT32", "exFAT"];

    private const UnixFileMode OthersSee = UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private const UnixFileMode OthersWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    /// <summary>Judges <paramref name="given"/> as the base of this side's archive.</summary>
    /// <param name="cleanupRoots">The folders the registry's actions clean (<see cref="ExtraAgentRules.CleanupRoots"/>).</param>
    public static BaseFolderReport Judge(IHostPaths paths, IFileSystem files, IReadOnlyList<ForbiddenFolder> cleanupRoots, string given)
    {
        var side = paths.Side == HostSide.Wsl ? "wsl" : "windows";
        var placed = paths is LinuxHostPaths linux ? PlaceInDistro(linux, files, given) : PlaceOnWindows(given);
        return placed switch
        {
            Placement.Refused refused => Refused(side, given, string.Empty, null, refused.Rule, refused.Why),
            Placement.Placed at => Judged(side, given, at, paths, files, cleanupRoots),
            _ => throw new System.Diagnostics.UnreachableException("Placement is a closed set"),
        };
    }

    /// <summary>Where a base was placed, or the rule that refused it before any folder was looked at.</summary>
    private abstract record Placement
    {
        private Placement()
        {
        }

        public sealed record Placed(string Folder, string OnDisk, MountView Mount) : Placement;

        public sealed record Refused(string Rule, string Why) : Placement;
    }

    /// <summary>The filesystem as the judgement needs it.</summary>
    private sealed record MountView(BaseMountReport Report, bool Drvfs, bool IsDistroDisk)
    {
        public static readonly MountView Unknown = new(new BaseMountReport(string.Empty, string.Empty, string.Empty), false, false);

        public bool Known => Report.Type.Length > 0;
    }

    private static Placement PlaceInDistro(LinuxHostPaths paths, IFileSystem files, string given)
    {
        if (ShapeProblem(given) is { Length: > 0 } shape)
        {
            return new Placement.Refused(BaseFolderRule.Shape, shape);
        }

        if (given.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return new Placement.Refused(BaseFolderRule.Shape, $"{given} is a Windows share; inside the distribution name the folder where the share is mounted (a drvfs mount of it)");
        }

        return ProcText.Read(files, paths.Rules.Join(paths.ProcRoot, "self", "mountinfo")) switch
        {
            Reading<string>.Available table => InTable(paths, given, MountTable.Parse(table.Value)),
            var unreadable => new Placement.Refused(BaseFolderRule.MountUnreadable, $"the mount table could not be read ({unreadable.ReasonOrEmpty}), so the filesystem the base lies on is unknown"),
        };
    }

    private static Placement InTable(LinuxHostPaths paths, string given, IReadOnlyList<MountEntry> mounts) =>
        given.StartsWith('/')
            ? Placed(paths, given, mounts)
            : DriveMount(mounts, given[0]) is { } drive
                ? Placed(paths, drive.MountPoint.TrimEnd('/') + "/" + given[3..].Replace('\\', '/').TrimEnd('/'), mounts)
                : new Placement.Refused(BaseFolderRule.DriveNotMounted, $"drive {char.ToUpperInvariant(given[0])}: is not mounted in this distribution (no drvfs mount of the whole drive in the mount table); mount it, or choose a folder the distribution sees");

    private static MountEntry? DriveMount(IReadOnlyList<MountEntry> mounts, char letter) => mounts.LastOrDefault(m => MountTable.IsWholeDrive(m, letter));

    private static Placement Placed(LinuxHostPaths paths, string distro, IReadOnlyList<MountEntry> mounts)
    {
        var folder = distro.Length > 1 ? distro.TrimEnd('/') : distro;
        return MountTable.Holding(mounts, folder) is { } mount
            ? new Placement.Placed(folder, paths.DistroPath(folder), new MountView(new BaseMountReport(mount.MountPoint, mount.Type, mount.Source), mount.IsDrvfs, mount.MountPoint == "/"))
            : new Placement.Refused(BaseFolderRule.MountUnreadable, $"the mount table names no filesystem holding {folder}, so what it lies on is unknown");
    }

    private static Placement PlaceOnWindows(string given)
    {
        if (ShapeProblem(given) is { Length: > 0 } shape)
        {
            return new Placement.Refused(BaseFolderRule.Shape, shape);
        }

        return given.StartsWith('/')
            ? new Placement.Refused(BaseFolderRule.Shape, $"{given} is a Linux path; the Windows side names a drive folder (V:\\…) or a share (\\\\server\\share\\…)")
            : new Placement.Placed(given.TrimEnd('\\'), given.TrimEnd('\\'), WindowsMount(given));
    }

    /// <summary>The drive's kind and format, or the share — the Windows side's "mount".</summary>
    private static MountView WindowsMount(string folder)
    {
        if (folder.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var share = string.Join('\\', folder[2..].Split('\\').Take(2));
            return new MountView(new BaseMountReport($@"\\{share}", "network", share), false, false);
        }

        try
        {
            var drive = new DriveInfo(folder[..1]);
            var format = drive.IsReady ? drive.DriveFormat : "not ready";
            var kind = drive.DriveType.ToString().ToLowerInvariant();
            return new MountView(new BaseMountReport(drive.RootDirectory.FullName, $"{kind} {format}", drive.Name), false, false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return MountView.Unknown;
        }
    }

    private static BaseFolderReport Judged(string side, string given, Placement.Placed at, IHostPaths paths, IFileSystem files, IReadOnlyList<ForbiddenFolder> cleanupRoots)
    {
        var refusal = FolderRefusal(at, paths, files, cleanupRoots);
        return refusal is not null
            ? Refused(side, given, at.Folder, at.Mount.Known ? at.Mount.Report : null, refusal.Value.Rule, refusal.Value.Why)
            : new BaseFolderReport(SchemaVersion.Current, side, given, true, at.Folder, string.Empty, string.Empty, at.Mount.Known ? at.Mount.Report : null, Warnings(at, paths), Notes(at));
    }

    /// <summary>What one rule looks at.</summary>
    private sealed record Check(Placement.Placed At, IHostPaths Paths, IFileSystem Files, IReadOnlyList<ForbiddenFolder> CleanupRoots);

    /// <summary>The rules, in the order of the remarks — each asked only when every one before it held.</summary>
    private static readonly Func<Check, (string Rule, string Why)?>[] Steps =
    [
        c => ExistenceProblem(c.At, c.Files),
        c => LinkProblem(c.At, c.Files),
        c => RootProblem(c.At),
        c => HomeProblem(c.At, c.Paths, c.Files),
        c => OverlapProblem(c.At, c.Paths, c.Files, c.CleanupRoots),
        c => FilesystemProblem(c.At),
        c => WriteProblem(c.At, c.Files),
    ];

    /// <summary>The first rule the placed folder breaks; <c>null</c> when it breaks none.</summary>
    private static (string Rule, string Why)? FolderRefusal(Placement.Placed at, IHostPaths paths, IFileSystem files, IReadOnlyList<ForbiddenFolder> cleanupRoots)
    {
        var check = new Check(at, paths, files, cleanupRoots);
        return Steps.Select(step => step(check)).FirstOrDefault(problem => problem is not null);
    }

    private static (string, string)? ExistenceProblem(Placement.Placed at, IFileSystem files) =>
        files.DirectoryExists(at.OnDisk) ? null
        : files.FileExists(at.OnDisk) ? (BaseFolderRule.NotAFolder, $"{at.Folder} is a file, not a folder")
        : (BaseFolderRule.Missing, $"{at.Folder} does not exist; the archive's folder is never created — create it, then choose it");

    /// <summary>Its real path must be its spelling: a link anywhere on the way would let what the folder IS change after it was judged.</summary>
    private static (string, string)? LinkProblem(Placement.Placed at, IFileSystem files) => files.ResolvePath(at.OnDisk) switch
    {
        RealPathResult.Resolved real when Rules.PathEquals(real.Path, Path.GetFullPath(at.OnDisk)) => null,
        RealPathResult.Resolved real => (BaseFolderRule.LinkOnTheWay, $"{at.Folder} is reached through a link (it leads to {real.Path}); choose the folder it leads to"),
        RealPathResult.Unresolvable u => (BaseFolderRule.LinkOnTheWay, $"{at.Folder} cannot be inspected for a link at {u.Component} ({u.Reason})"),
        _ => throw new System.Diagnostics.UnreachableException("RealPathResult is a closed set"),
    };

    private static (string, string)? RootProblem(Placement.Placed at) =>
        Rules.IsRoot(at.OnDisk) || IsMountPoint(at) || IsShareRoot(at.Folder)
            ? (BaseFolderRule.TooBroad, $"{at.Folder} is the root of a drive, a share or a filesystem; choose a folder inside it")
            : null;

    private static (string, string)? HomeProblem(Placement.Placed at, IHostPaths paths, IFileSystem files) =>
        Rules.PathEquals(RealOrFull(files, at.OnDisk), RealOrFull(files, paths.Home))
            ? (BaseFolderRule.TooBroad, $"{at.Folder} is the home folder itself; choose a folder for the archive")
            : null;

    private static bool IsMountPoint(Placement.Placed at) => at.Mount.Known && Rules.PathEquals(at.Folder, Trimmed(at.Mount.Report.MountPoint));

    /// <summary>A path without a trailing separator — except a root, which IS its separator.</summary>
    private static string Trimmed(string path) => path.Length > 1 ? path.TrimEnd('/', '\\') : path;

    private static bool IsShareRoot(string folder) => folder.StartsWith(@"\\", StringComparison.Ordinal) && folder[2..].Split('\\').Length <= 2;

    private static (string, string)? OverlapProblem(Placement.Placed at, IHostPaths paths, IFileSystem files, IReadOnlyList<ForbiddenFolder> cleanupRoots)
    {
        var real = RealOrFull(files, at.OnDisk);
        return Protected(paths, cleanupRoots).FirstOrDefault(f => ExtraAgentRules.Overlaps(real, RealOrFull(files, f.Path))) is { } clash
            ? (BaseFolderRule.Overlap, $"{at.Folder} is, holds or lies inside {clash.Whose} — the archive must never mix with what it moves or what a cleanup removes")
            : null;
    }

    /// <summary>Every agent root of this side (catalogue and manual), and the places a manual agent's folder may not overlap either;
    /// plus the temporary folder.</summary>
    private static IReadOnlyList<ForbiddenFolder> Protected(IHostPaths paths, IReadOnlyList<ForbiddenFolder> cleanupRoots) =>
    [
        .. ExtraAgentRules.ProtectedPlaces(paths, paths.AgentRoots.Select(r => new ForbiddenFolder(r, $"the AI agent folder {r}")), cleanupRoots),
        new ForbiddenFolder(paths.TempDirectory, $"the temporary folder {paths.TempDirectory}"),
    ];

    private static (string, string)? FilesystemProblem(Placement.Placed at) =>
        at.Mount.Known && VolatileTypes.Contains(at.Mount.Report.Type, StringComparer.OrdinalIgnoreCase)
            ? (BaseFolderRule.Volatile, $"{at.Folder} lies on {at.Mount.Report.Type} at {at.Mount.Report.MountPoint}, which keeps nothing across a shutdown")
            : at.Mount.Report.Type.StartsWith("ram ", StringComparison.Ordinal)
                ? (BaseFolderRule.Volatile, $"{at.Folder} lies on a RAM disk, which keeps nothing across a shutdown")
                : null;

    private static (string, string)? WriteProblem(Placement.Placed at, IFileSystem files) => files.ProbeExistingWriteAccess(at.OnDisk) switch
    {
        WriteAccess.Writable => null,
        WriteAccess.NotWritable no => (BaseFolderRule.NotWritable, no.Reason),
        _ => throw new System.Diagnostics.UnreachableException("WriteAccess is a closed set"),
    };

    private static IReadOnlyList<string> Warnings(Placement.Placed at, IHostPaths paths) =>
    [
        .. at.Mount.IsDistroDisk ? [$"{at.Folder} lies on the distribution's own disk: the archive lives and dies with the distribution — a folder on a Windows drive or a share outlives it"] : Array.Empty<string>(),
        .. ReaderWarnings(at, paths),
    ];

    /// <summary>Review M6: archived sessions hold what the agents saw — secrets included. Mode bits on Linux (not on drvfs, whose
    /// modes are the mount's report, not an answer); the folder's access control on Windows.</summary>
    private static IEnumerable<string> ReaderWarnings(Placement.Placed at, IHostPaths paths) =>
        OperatingSystem.IsWindows() ? WindowsAccess.Warnings(at.OnDisk, at.Folder)
        : at.Mount.Drvfs ? []
        : ModeWarnings(at, paths);

    [UnsupportedOSPlatform("windows")]
    private static IEnumerable<string> ModeWarnings(Placement.Placed at, IHostPaths paths)
    {
        var own = File.GetUnixFileMode(at.OnDisk);
        var see = (own & OthersSee) != 0 ? [$"other accounts may read {at.Folder} (mode {Octal(own)}): archived sessions hold what the agents saw — chmod 700 it"] : Array.Empty<string>();
        return [.. see, .. Ancestors(at, paths).Where(IsOpenToWriters).Select(a => $"{a.Folder} is writable by other accounts (mode {Octal(a.Mode)}) without the sticky bit: one of them could replace the archive's folder")];
    }

    [UnsupportedOSPlatform("windows")]
    private static IEnumerable<(string Folder, UnixFileMode Mode)> Ancestors(Placement.Placed at, IHostPaths paths)
    {
        var onDisk = Path.GetDirectoryName(Path.GetFullPath(at.OnDisk));
        var distro = Path.GetDirectoryName(at.Folder.Replace('/', Path.DirectorySeparatorChar));
        for (; onDisk is not null && distro is not null; onDisk = Path.GetDirectoryName(onDisk), distro = Path.GetDirectoryName(distro))
        {
            yield return (distro.Replace(Path.DirectorySeparatorChar, '/'), File.GetUnixFileMode(onDisk));
        }
    }

    private static bool IsOpenToWriters((string Folder, UnixFileMode Mode) ancestor) =>
        (ancestor.Mode & OthersWrite) != 0 && (ancestor.Mode & UnixFileMode.StickyBit) == 0;

    private static string Octal(UnixFileMode mode) => Convert.ToString((int)mode, 8).PadLeft(4, '0');

    private static IReadOnlyList<string> Notes(Placement.Placed at) =>
    [
        .. CoarseTimeTypes.Any(t => at.Mount.Report.Type.Contains(t, StringComparison.OrdinalIgnoreCase)) ? ["this filesystem keeps a last write to 2 seconds; the index's recorded time is the authority, the archived file's own is never compared"] : Array.Empty<string>(),
        .. at.Mount.Drvfs ? ["a drvfs mount: its modes are what the mount reports — Windows decides who may read it"] : Array.Empty<string>(),
    ];

    private static string ShapeProblem(string given) =>
        given.Length == 0 ? "no folder was given"
        : new Config.TextRule.AbsolutePathOrEmpty().Problem(given) is { Length: > 0 } problem ? $"{given} is {problem}"
        : string.Empty;

    private static BaseFolderReport Refused(string side, string given, string folder, BaseMountReport? mount, string rule, string why) =>
        new(SchemaVersion.Current, side, given, false, folder, rule, why, mount, [], []);

    private static string RealOrFull(IFileSystem files, string path) =>
        files.ResolvePath(path) is RealPathResult.Resolved r ? r.Path : Path.GetFullPath(path);

    private static PathRules Rules => PathRules.ForThisOs;
}
