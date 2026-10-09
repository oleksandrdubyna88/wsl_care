using System.Runtime.Versioning;

using WslCare.Core.Agents;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Archive;

/// <summary>The filesystem the base lies on, as the side judging it found it.</summary>
/// <param name="MountPoint">Where it is mounted (a distro path), the drive's root or the share on Windows.</param>
/// <param name="Type">The filesystem type (<c>ext4</c>, <c>9p</c>, <c>tmpfs</c>) or, on Windows, the drive's kind and format
/// (<c>network</c>, <c>fixed NTFS</c>).</param>
/// <param name="Source">What is mounted there (a device, a drive, a share).</param>
public sealed record BaseMountReport(string MountPoint, string Type, string Source)
{
    /// <summary>No filesystem established (a refusal before it, or an unreadable table): every field empty, never null.</summary>
    public static readonly BaseMountReport Unknown = new(string.Empty, string.Empty, string.Empty);

    public bool Known => Type.Length > 0;
}

/// <summary>What the judge needs beyond the host: the folders this side's cleanups clean, and the Windows profile a full run found
/// (<c>C:\Users\me</c>; empty when no run has found it) — inside the distribution a base on a Windows drive is judged against that
/// profile's places too (E9.S0 review round S1).</summary>
public sealed record BaseFolderContext(IReadOnlyList<ForbiddenFolder> CleanupRoots, string WindowsProfile)
{
    /// <summary>The data folders of the manual agents the WINDOWS side walks (<c>aiAgents.extra</c>, side <c>windows</c>), as Windows
    /// spells them — protected from the distro too (E9.S1 review round M3).</summary>
    public IReadOnlyList<string> WindowsAgentFolders { get; init; } = [];
}

/// <summary>
/// What <c>archive check-base</c> answers (plan §15r D7, E9.S0): the base as THIS side sees it, accepted or refused by one named
/// rule, the filesystem it lies on, what a person should know (who else may read it, a coarse time) and nothing else.
/// </summary>
/// <param name="Side"><c>wsl</c> or <c>windows</c>.</param>
/// <param name="Given">The path as it was asked about.</param>
/// <param name="Folder">The base as this side spells it — a Windows drive path asked of the distro is answered with the Linux path
/// it is mounted at, a folder reached through a bind mount with the folder it really is; empty when it could not be placed.</param>
/// <param name="Rule">The rule that refused it (<see cref="BaseFolderRule"/>); empty when accepted.</param>
/// <param name="Refusal">The sentence; empty when accepted.</param>
/// <param name="Mount">The filesystem; <see cref="BaseMountReport.Unknown"/> (every field empty) when it was not established.</param>
/// <param name="Warnings">Accepted, but a person should act: other accounts may read it or own a folder on the way, it lives on the
/// distro's own disk.</param>
/// <param name="Notes">Facts that change no decision: a 2-second time granularity, drvfs's reported modes, a bind mount.</param>
public sealed record BaseFolderReport(
    int SchemaVersion,
    string Side,
    string Given,
    bool Accepted,
    string Folder,
    string Rule,
    string Refusal,
    BaseMountReport Mount,
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

    /// <summary>The base not judged YET: a run's input before its bounded window judged it (the S4 gate round, findings 1 and 3).</summary>
    public const string NotJudged = "not-judged";

    public static IReadOnlyList<string> All { get; } = [Shape, DriveNotMounted, MountUnreadable, Missing, NotAFolder, LinkOnTheWay, TooBroad, Overlap, Volatile, NotWritable, NotJudged];
}

/// <summary>
/// Plan §15r D7 — where the archive may live, judged by the process that will write there (the TARGET USER's, never root's, D1),
/// at <c>config set archive.baseFolder</c>, by <c>archive check-base</c> and again at the start of every run: never trusted
/// because it held once. In order: the shape (on Windows a share that is this machine or an administrative share is refused); on
/// the distro a Windows drive path is placed at its drvfs mount and a bind mount at the folder it really is; an EXISTING folder,
/// never created; no link on any component (its real path is its spelling); not a filesystem, drive or share root, not the home;
/// not equal to, inside or containing an agent's folder, the repositories folder, Claude's temporary folders, a folder a cleanup
/// cleans, the product's own folders, the temporary folder — on Windows compared by the file system's identity too, and in the
/// distro, for a base on a Windows drive, also the Windows profile's places, case-blind; not a filesystem that is gone after a
/// shutdown; writable by this process. Accepted, it may still carry warnings — other accounts may read it (archived sessions hold
/// what the agents saw) or own a folder on the way, it lives on the distro's own disk — and notes.
/// </summary>
/// <remarks>The judge reads: the kernel's mount table (a System read), names, attributes, owners and, on Windows, file identities;
/// it creates only the probe file a write test needs, inside the folder, deleted on close. Its verdict is advice at the moment it is
/// given: the run binds itself to the base through its own no-follow descriptor chain (§15r, E9.S2a/S2b), never through this.</remarks>
public static partial class BaseFolderRules
{
    /// <summary>Filesystems a shutdown empties or that hold no files of their own.</summary>
    private static readonly string[] VolatileTypes = ["tmpfs", "ramfs", "proc", "sysfs", "devtmpfs", "overlay", "devpts", "cgroup", "cgroup2"];

    /// <summary>Filesystems that keep a last write to two seconds (FAT) — the index's recorded time is the authority (§15r G6).</summary>
    private static readonly string[] CoarseTimeTypes = ["vfat", "msdos", "exfat", "FAT", "FAT32", "exFAT"];

    private const UnixFileMode OthersSee = UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private const UnixFileMode OthersWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    /// <summary>Judges <paramref name="given"/> as the base of this side's archive, the Windows profile unknown.</summary>
    /// <param name="cleanupRoots">The folders the registry's actions clean (<see cref="ExtraAgentRules.CleanupRoots"/>).</param>
    /// <summary>The answer for "no base configured" — never judged, never accepted (E9.S2b: the run answers <c>no-base</c>).</summary>
    /// <summary>What a run's input holds until its base is judged inside the bounded window (<see cref="BaseWindow"/>; the S4 gate round,
    /// findings 1 and 3): refused under its own rule, so whatever reads it before the window says "not judged yet" — never "no base",
    /// never a base it may use.</summary>
    public static BaseFolderReport NotYetJudged { get; } =
        new(SchemaVersion.Current, string.Empty, string.Empty, false, string.Empty, BaseFolderRule.NotJudged, "the base is judged inside the bounded window of the run, not before it", BaseMountReport.Unknown, [], []);

    public static BaseFolderReport Unconfigured { get; } = new(SchemaVersion.Current, string.Empty, string.Empty, false, string.Empty, string.Empty, "no archive.baseFolder is set", BaseMountReport.Unknown, [], []);

    public static BaseFolderReport Judge(IHostPaths paths, IFileSystem files, IReadOnlyList<ForbiddenFolder> cleanupRoots, string given) =>
        Judge(paths, files, new BaseFolderContext(cleanupRoots, string.Empty), given);

    /// <summary>Judges <paramref name="given"/> as the base of this side's archive.</summary>
    public static BaseFolderReport Judge(IHostPaths paths, IFileSystem files, BaseFolderContext context, string given)
    {
        var side = paths.Side == HostSide.Wsl ? "wsl" : "windows";
        return Place(paths, files, given) switch
        {
            Placement.Refused refused => Refused(side, given, string.Empty, BaseMountReport.Unknown, refused.Rule, refused.Why),
            Placement.Placed at => Judged(side, given, new Check(at, paths, files, context)),
            _ => throw new System.Diagnostics.UnreachableException("Placement is a closed set"),
        };
    }

    private static BaseFolderReport Judged(string side, string given, Check check) => RuleVerdict.First(Steps, check) switch
    {
        RuleVerdict.Refuses refused => Refused(side, given, check.At.Folder, check.At.Mount.Report, refused.Rule, refused.Why),
        _ => new BaseFolderReport(SchemaVersion.Current, side, given, true, check.At.Folder, string.Empty, string.Empty, check.At.Mount.Report, Warnings(check.At, check.Paths, check.Context), Notes(check.At)),
    };

    /// <summary>What one rule looks at.</summary>
    private sealed record Check(Placement.Placed At, IHostPaths Paths, IFileSystem Files, BaseFolderContext Context);

    /// <summary>The rules, in the order of the summary — each asked only when every one before it held.</summary>
    private static readonly Func<Check, RuleVerdict>[] Steps =
    [
        c => ShortNameProblem(c.At),
        c => ExistenceProblem(c.At, c.Files),
        c => LinkProblem(c.At, c.Files),
        c => RootProblem(c.At),
        c => HomeProblem(c.At, c.Paths, c.Files),
        c => OverlapProblem(c.At, c.Paths, c.Files, c.Context.CleanupRoots),
        c => WindowsPlacesProblem(c.At, c.Context),
        c => FilesystemProblem(c.At),
        c => WriteProblem(c.At, c.Files),
    ];

    /// <summary>E9.S1 review round M2: drvfs resolves an 8.3 short name (<c>CLAUDE~1</c>) to the long one, while the distro's real path
    /// keeps the short spelling — the Windows places would be compared by a spelling they do not have. Refused on drvfs.</summary>
    private static RuleVerdict ShortNameProblem(Placement.Placed at) =>
        RuleVerdict.When(at.Mount.Drvfs && at.Folder.Split('/').Any(IsShortName), BaseFolderRule.Shape, () => $"{at.Folder} holds an 8.3 short name (a segment with ~ and a digit); on a Windows drive name the folder by its long name");

    private static bool IsShortName(string segment) => segment.IndexOf('~') is var tilde and >= 0 && tilde + 1 < segment.Length && char.IsAsciiDigit(segment[tilde + 1]);

    private static RuleVerdict ExistenceProblem(Placement.Placed at, IFileSystem files) =>
        files.DirectoryExists(at.OnDisk) ? RuleVerdict.Holds
        : files.FileExists(at.OnDisk) ? new RuleVerdict.Refuses(BaseFolderRule.NotAFolder, $"{at.Folder} is a file, not a folder")
        : new RuleVerdict.Refuses(BaseFolderRule.Missing, $"{at.Folder} does not exist; the archive's folder is never created — create it, then choose it");

    /// <summary>Its real path must be its spelling: a link anywhere on the way would let what the folder IS change after it was judged.</summary>
    private static RuleVerdict LinkProblem(Placement.Placed at, IFileSystem files) => files.ResolvePath(at.SpelledOnDisk) switch
    {
        RealPathResult.Resolved real when Rules.PathEquals(real.Path, Path.GetFullPath(at.SpelledOnDisk)) => RuleVerdict.Holds,
        RealPathResult.Resolved real => new RuleVerdict.Refuses(BaseFolderRule.LinkOnTheWay, $"{at.Folder} is reached through a link (it leads to {real.Path}); choose the folder it leads to"),
        RealPathResult.Unresolvable u => new RuleVerdict.Refuses(BaseFolderRule.LinkOnTheWay, $"{at.Folder} cannot be inspected for a link at {u.Component} ({u.Reason})"),
        _ => throw new System.Diagnostics.UnreachableException("RealPathResult is a closed set"),
    };

    private static RuleVerdict RootProblem(Placement.Placed at) =>
        RuleVerdict.When(Rules.IsRoot(at.OnDisk) || IsMountPoint(at) || IsShareRoot(at.Folder), BaseFolderRule.TooBroad, () => $"{at.Folder} is the root of a drive, a share or a filesystem; choose a folder inside it");

    private static RuleVerdict HomeProblem(Placement.Placed at, IHostPaths paths, IFileSystem files) =>
        RuleVerdict.When(Rules.PathEquals(RealOrFull(files, at.OnDisk), RealOrFull(files, paths.Home)), BaseFolderRule.TooBroad, () => $"{at.Folder} is the home folder itself; choose a folder for the archive");

    private static bool IsMountPoint(Placement.Placed at) => at.Mount.Report.Known && Rules.PathEquals(at.Folder, Trimmed(at.Mount.Report.MountPoint));

    /// <summary>A path without a trailing separator — except a root, which IS its separator.</summary>
    private static string Trimmed(string path) => path.Length > 1 ? path.TrimEnd('/', '\\') : path;

    private static bool IsShareRoot(string folder) => folder.StartsWith(@"\\", StringComparison.Ordinal) && folder[2..].Split('\\', '/').Length <= 2;

    /// <summary>Equal, inside or containing a protected place: by the real paths, and on Windows by the file system's identity of the
    /// folder and its parents as well — an 8.3 name, a <c>subst</c> drive or a second spelling of one volume is the same folder
    /// (E9.S0 review round S3).</summary>
    private static RuleVerdict OverlapProblem(Placement.Placed at, IHostPaths paths, IFileSystem files, IReadOnlyList<ForbiddenFolder> cleanupRoots)
    {
        var real = RealOrFull(files, at.OnDisk);
        var identity = OperatingSystem.IsWindows() ? WindowsIdentity.ChainOf(at.OnDisk) : [];
        return Protected(paths, cleanupRoots).FirstOrDefault(f => ExtraAgentRules.Overlaps(real, RealOrFull(files, f.Path)) || SameByIdentity(identity, f.Path)) is { } clash
            ? new RuleVerdict.Refuses(BaseFolderRule.Overlap, $"{at.Folder} is, holds or lies inside {clash.Whose} — the archive must never mix with what it moves or what a cleanup removes")
            : RuleVerdict.Holds;
    }

    private static bool SameByIdentity(IReadOnlyList<FileIdentity> baseChain, string place) =>
        baseChain.Count > 0 && OperatingSystem.IsWindows() && WindowsIdentity.Overlap(baseChain, WindowsIdentity.ChainOf(place));

    /// <summary>Every agent root of this side (catalogue and manual), and the places a manual agent's folder may not overlap either;
    /// plus the temporary folder.</summary>
    private static IReadOnlyList<ForbiddenFolder> Protected(IHostPaths paths, IReadOnlyList<ForbiddenFolder> cleanupRoots) =>
    [
        .. ExtraAgentRules.ProtectedPlaces(paths, paths.AgentRoots.Select(r => new ForbiddenFolder(r, $"the AI agent folder {r}")), cleanupRoots),
        new ForbiddenFolder(paths.TempDirectory, $"the temporary folder {paths.TempDirectory}"),
    ];

    /// <summary>E9.S0 review round S1: in the distro a base on a Windows drive is ALSO a Windows folder — judged against the Windows
    /// profile's places (agent folders, the temporary folder, Claude's, the repositories, wsl-care's own), case-blind as NTFS
    /// compares, and against any profile's AppData and agent folders whoever's they are.</summary>
    private static RuleVerdict WindowsPlacesProblem(Placement.Placed at, BaseFolderContext context) =>
        at.WindowsSpelling.Length == 0 ? RuleVerdict.Holds
        : WindowsProfilePlaces.Clash(at.WindowsSpelling, context.WindowsProfile, context.WindowsAgentFolders) is { Length: > 0 } whose
            ? new RuleVerdict.Refuses(BaseFolderRule.Overlap, $"{at.Folder} ({at.WindowsSpelling} on Windows) is, holds or lies inside {whose} — the archive must never mix with what it moves")
            : RuleVerdict.Holds;

    private static RuleVerdict FilesystemProblem(Placement.Placed at) =>
        at.Mount.Report.Known && VolatileTypes.Contains(at.Mount.Report.Type, StringComparer.OrdinalIgnoreCase)
            ? new RuleVerdict.Refuses(BaseFolderRule.Volatile, $"{at.Folder} lies on {at.Mount.Report.Type} at {at.Mount.Report.MountPoint}, which keeps nothing across a shutdown")
            : RuleVerdict.When(at.Mount.Report.Type.StartsWith("ram ", StringComparison.Ordinal), BaseFolderRule.Volatile, () => $"{at.Folder} lies on a RAM disk, which keeps nothing across a shutdown");

    private static RuleVerdict WriteProblem(Placement.Placed at, IFileSystem files) => files.ProbeExistingWriteAccess(at.OnDisk) switch
    {
        WriteAccess.Writable => RuleVerdict.Holds,
        WriteAccess.NotWritable no => new RuleVerdict.Refuses(BaseFolderRule.NotWritable, no.Reason),
        _ => throw new System.Diagnostics.UnreachableException("WriteAccess is a closed set"),
    };

    private static IReadOnlyList<string> Warnings(Placement.Placed at, IHostPaths paths, BaseFolderContext context) =>
    [
        .. UnknownProfileWarning(at, context),
        .. at.Mount.IsDistroDisk ? [$"{at.Folder} lies on the distribution's own disk: the archive lives and dies with the distribution — a folder on a Windows drive or a share outlives it"] : Array.Empty<string>(),
        .. ReaderWarnings(at, paths),
    ];

    /// <summary>E9.S1 review round m1: without the Windows profile (no full run has found it — normal at install) only the places
    /// every profile has were judged on a Windows drive; said, never silent. A warning, not a refusal: the patterns cover every
    /// profile's AppData, agent folders and repositories and wsl-care's own folder, and a person setting up before the first run
    /// must not be stopped by a fact a run will supply.</summary>
    private static IEnumerable<string> UnknownProfileWarning(Placement.Placed at, BaseFolderContext context) =>
        at.WindowsSpelling.Length > 0 && context.WindowsProfile.Length == 0
            ? [$"the Windows profile is unknown (no full run has found it yet): {at.WindowsSpelling} was judged against the places every profile has, not against your profile's own — run a full check (wsl-care collect, or wait for the timer), then check the base again"]
            : [];

    /// <summary>Review M6: archived sessions hold what the agents saw — secrets included. Mode bits and owners on Linux (not on drvfs,
    /// whose modes and owners are the mount's report, not an answer); the folder's access control on Windows.</summary>
    private static IEnumerable<string> ReaderWarnings(Placement.Placed at, IHostPaths paths) =>
        OperatingSystem.IsWindows() ? WindowsAccess.Warnings(at.OnDisk, at.Folder)
        : at.Mount.Drvfs ? []
        : ModeWarnings(at, paths);

    [UnsupportedOSPlatform("windows")]
    private static IEnumerable<string> ModeWarnings(Placement.Placed at, IHostPaths paths)
    {
        var own = File.GetUnixFileMode(at.OnDisk);
        var see = (own & OthersSee) != 0 ? [$"other accounts may read {at.Folder} (mode {Octal(own)}): archived sessions hold what the agents saw — chmod 700 it"] : Array.Empty<string>();
        var chain = Chain(at).ToList();
        return
        [
            .. see,
            .. chain.Skip(1).Where(IsOpenToWriters).Select(a => $"{a.Folder} is writable by other accounts (mode {Octal(a.Mode)}) without the sticky bit: one of them could replace the archive's folder"),
            .. OwnerWarnings(chain.Select(a => (a.Folder, a.Owner)), RegularFiles.EffectiveUid()),
        ];
    }

    /// <summary>The base and every folder above it: its distro spelling, its mode and its owner.</summary>
    [UnsupportedOSPlatform("windows")]
    private static IEnumerable<(string Folder, UnixFileMode Mode, uint Owner)> Chain(Placement.Placed at)
    {
        var onDisk = Path.GetFullPath(at.OnDisk);
        var distro = at.Folder.Replace('/', Path.DirectorySeparatorChar);
        for (; onDisk is not null && distro is not null; onDisk = Path.GetDirectoryName(onDisk), distro = Path.GetDirectoryName(distro))
        {
            yield return (distro.Replace(Path.DirectorySeparatorChar, '/'), File.GetUnixFileMode(onDisk), OwnerOf(onDisk));
        }
    }

    /// <summary>The owner of a folder, or root's uid when it cannot be read (an unreadable owner is no warning by itself).</summary>
    private static uint OwnerOf(string folder) => OperatingSystem.IsLinux() && RegularFiles.StatNoFollow(folder) is Collectors.Reading<FileStatus>.Available status ? status.Value.OwnerUid : 0;

    /// <summary>E9.S0 review round S5: the folders of <paramref name="chain"/> (the base and every folder above it) owned by an account
    /// other than root and this one — that account could rename the archive's folder away, or put another in its place.</summary>
    public static IReadOnlyList<string> OwnerWarnings(IEnumerable<(string Folder, uint Owner)> chain, uint me) =>
        [.. chain.Where(c => c.Owner != 0 && c.Owner != me).Select(c => $"{c.Folder} is owned by another account (uid {c.Owner}): it could move or replace the archive's folder")];

    private static bool IsOpenToWriters((string Folder, UnixFileMode Mode, uint Owner) ancestor) =>
        (ancestor.Mode & OthersWrite) != 0 && (ancestor.Mode & UnixFileMode.StickyBit) == 0;

    private static string Octal(UnixFileMode mode) => Convert.ToString((int)mode, 8).PadLeft(4, '0');

    private static IReadOnlyList<string> Notes(Placement.Placed at) =>
    [
        .. at.Notes,
        .. CoarseTimeTypes.Any(t => at.Mount.Report.Type.Contains(t, StringComparison.OrdinalIgnoreCase)) ? ["this filesystem keeps a last write to 2 seconds; the index's recorded time is the authority, the archived file's own is never compared"] : Array.Empty<string>(),
        .. at.Mount.Drvfs ? ["a drvfs mount: its modes are what the mount reports — Windows decides who may read it"] : Array.Empty<string>(),
    ];

    private static BaseFolderReport Refused(string side, string given, string folder, BaseMountReport mount, string rule, string why) =>
        new(SchemaVersion.Current, side, given, false, folder, rule, why, mount, [], []);

    private static string RealOrFull(IFileSystem files, string path) =>
        files.ResolvePath(path) is RealPathResult.Resolved r ? r.Path : Path.GetFullPath(path);

    private static PathRules Rules => PathRules.ForThisOs;
}
