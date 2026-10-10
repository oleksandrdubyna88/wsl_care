using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Archive;

/// <summary>
/// Where a base lies before any folder is looked at (plan §15r D7, E9.S0 review round S3, S4, E9.S1 review round B1, m2, m6, m7):
/// the spelling refused when it holds an empty or <c>.</c> segment; the REAL path judged (links resolved — the link rule still
/// refuses one on the way); in the distro, placed by the mount table — a Windows drive path at its drvfs mount, a bind or second
/// mount at the folder it really is, the distribution's own disk known by its DEVICE, a drvfs mount of a share back to this
/// machine refused; on Windows, a share refused when it is this machine or an administrative share.
/// </summary>
public static partial class BaseFolderRules
{
    /// <summary>Where a base was placed, or the rule that refused it before any folder was looked at.</summary>
    private abstract record Placement
    {
        private Placement()
        {
        }

        /// <param name="Folder">The base as this side spells it — its REAL path.</param>
        /// <param name="OnDisk">The same as this process opens it (under the sandbox root when sandboxed).</param>
        /// <param name="SpelledOnDisk">The path as it was given, as this process opens it — what the link rule resolves.</param>
        /// <param name="WindowsSpelling">On a drvfs mount, the folder as Windows spells it (<c>C:\Users\me\x</c>); empty otherwise.</param>
        /// <param name="Notes">What the placement itself found (a bind mount followed).</param>
        public sealed record Placed(string Folder, string OnDisk, string SpelledOnDisk, MountView Mount, string WindowsSpelling, IReadOnlyList<string> Notes) : Placement;

        public sealed record Refused(string Rule, string Why) : Placement;
    }

    /// <summary>The filesystem as the judgement needs it.</summary>
    private sealed record MountView(BaseMountReport Report, bool Drvfs, bool IsDistroDisk);

    private static Placement Place(IHostPaths paths, IFileSystem files, string given) =>
        ShapeProblem(given) is { Length: > 0 } shape ? new Placement.Refused(BaseFolderRule.Shape, shape)
        : paths is LinuxHostPaths linux ? PlaceInDistro(linux, files, given)
        : PlaceOnWindows(files, given);

    private static string ShapeProblem(string given) =>
        given.Length == 0 ? "no folder was given"
        : new Config.TextRule.AbsolutePathOrEmpty().Problem(given) is { Length: > 0 } problem ? $"{given} is {problem}"
        : string.Empty;

    private static Placement PlaceInDistro(LinuxHostPaths paths, IFileSystem files, string given) =>
        given.StartsWith(@"\\", StringComparison.Ordinal)
            ? new Placement.Refused(BaseFolderRule.Shape, $"{given} is a Windows share; inside the distribution name the folder where the share is mounted (a drvfs mount of it)")
            : ProcText.Read(files, paths.Rules.Join(paths.ProcRoot, "self", "mountinfo")) switch
            {
                Reading<string>.Available table => InTable(paths, files, given, MountTable.Parse(table.Value)),
                var unreadable => new Placement.Refused(BaseFolderRule.MountUnreadable, $"the mount table could not be read ({unreadable.ReasonOrEmpty}), so the filesystem the base lies on is unknown"),
            };

    private static Placement InTable(LinuxHostPaths paths, IFileSystem files, string given, IReadOnlyList<MountEntry> mounts) =>
        given.StartsWith('/')
            ? AtRealPath(paths, files, Trimmed(given), mounts)
            : DriveMount(mounts, given[0]) is { } drive
                ? AtRealPath(paths, files, Trimmed(drive.MountPoint.TrimEnd('/') + "/" + given[3..].Replace('\\', '/')), mounts)
                : new Placement.Refused(BaseFolderRule.DriveNotMounted, $"drive {char.ToUpperInvariant(given[0])}: is not mounted in this distribution (no drvfs mount of the whole drive in the mount table); mount it, or choose a folder the distribution sees");

    private static MountEntry? DriveMount(IReadOnlyList<MountEntry> mounts, char letter) => mounts.LastOrDefault(m => MountTable.IsWholeDrive(m, letter));

    /// <summary>B1: the mount checks run on the folder's REAL path (links resolved), never on its spelling.</summary>
    private static Placement AtRealPath(LinuxHostPaths paths, IFileSystem files, string spelled, IReadOnlyList<MountEntry> mounts)
    {
        var spelledOnDisk = paths.DistroPath(spelled);
        var real = files.ResolvePath(spelledOnDisk) is RealPathResult.Resolved resolved ? Trimmed(paths.ToDistro(resolved.Path)) : spelled;
        return NotALoopbackShare(Placed(paths, real, spelledOnDisk, mounts));
    }

    private static Placement Placed(LinuxHostPaths paths, string folder, string spelledOnDisk, IReadOnlyList<MountEntry> mounts) => MountTable.Holding(mounts, folder) switch
    {
        null => new Placement.Refused(BaseFolderRule.MountUnreadable, $"the mount table names no filesystem holding {folder}, so what it lies on is unknown"),
        var mount when BaseOf(mount, mounts) == mount => new Placement.Placed(folder, paths.DistroPath(folder), spelledOnDisk, View(mount, mounts), WindowsSpelling(mount, folder), []),
        var alias => ThroughAlias(paths, folder, spelledOnDisk, alias, mounts),
    };

    /// <summary>The mount a mount's folders are judged under (E9.S1 review round m7): among the mounts of the same device, the one
    /// whose root is the mount's own root or an ancestor of it — the most whole one, the distribution's <c>/</c> first, then the
    /// shortest mount point. A mount that is the only one of its device's subtree (a btrfs subvolume mounted on its own, a bind of a
    /// folder whose filesystem nothing else shows) is its own: nothing visible aliases it.</summary>
    private static MountEntry BaseOf(MountEntry mount, IReadOnlyList<MountEntry> mounts) =>
        mounts.Where(m => m.Major == mount.Major && m.Minor == mount.Minor && IsSameOrUnder(mount.Root, m.Root))
            .OrderBy(m => m.Root.Length).ThenBy(m => m.MountPoint == "/" ? 0 : 1).ThenBy(m => m.MountPoint.Length).ThenBy(m => m.MountPoint, StringComparer.Ordinal)
            .First();

    private static bool IsSameOrUnder(string root, string ancestor) =>
        ancestor == "/" || root == ancestor || root.StartsWith(ancestor.TrimEnd('/') + "/", StringComparison.Ordinal);

    /// <summary>E9.S0 review round S4: a bind mount — or a second mount of a filesystem — shows its folders under a new name:
    /// <c>/mnt/x</c> may BE <c>~/.claude</c>. The base is judged at the folder it really is, under its device's base mount.</summary>
    private static Placement ThroughAlias(LinuxHostPaths paths, string folder, string spelledOnDisk, MountEntry alias, IReadOnlyList<MountEntry> mounts)
    {
        var whole = BaseOf(alias, mounts);
        var real = RootedOrSlash(Trimmed(whole.MountPoint.TrimEnd('/') + alias.Root[whole.Root.TrimEnd('/').Length..].TrimEnd('/') + folder[alias.MountPoint.TrimEnd('/').Length..]));
        return MountTable.Holding(mounts, real) is { } holding && BaseOf(holding, mounts) == holding
            ? new Placement.Placed(real, paths.DistroPath(real), spelledOnDisk, View(holding, mounts), WindowsSpelling(holding, real), [$"{folder} is {alias.Root} of the filesystem mounted at {whole.MountPoint} (a bind or second mount at {alias.MountPoint}): judged where it really lies, {real}"])
            : new Placement.Refused(BaseFolderRule.LinkOnTheWay, $"{folder} is {real} through a bind mount, and {real} is itself reached through another: refused rather than followed further");
    }

    /// <summary>E9.S1 review round m6: the mount point of a second mount of the root disk IS <c>/</c>, never an empty path.</summary>
    private static string RootedOrSlash(string path) => path.Length == 0 ? "/" : path;

    /// <summary>The distribution's own disk is its DEVICE (or its source), not the mount point <c>/</c>: a second mount of it is the
    /// same disk (E9.S0 review round S4).</summary>
    private static MountView View(MountEntry mount, IReadOnlyList<MountEntry> mounts) =>
        new(new BaseMountReport(mount.MountPoint, mount.Type, mount.Source), mount.IsDrvfs, mounts.LastOrDefault(m => m.MountPoint == "/") is { } root && SameDisk(mount, root));

    private static bool SameDisk(MountEntry mount, MountEntry root) =>
        (mount.Major == root.Major && mount.Minor == root.Minor) || (root.Source.StartsWith("/dev/", StringComparison.Ordinal) && mount.Source == root.Source);

    /// <summary>A drvfs folder as Windows spells it: the mounted drive or share and the rest of the path; empty for any other
    /// filesystem.</summary>
    private static string WindowsSpelling(MountEntry mount, string folder) =>
        DrvfsRoot(mount) is { Length: >= 2 } root ? JoinWindows(root, folder[mount.MountPoint.TrimEnd('/').Length..].Trim('/').Replace('/', '\\')) : string.Empty;

    /// <summary>What a drvfs mount mounts — its <c>path=</c> option, else its source; empty when it is no drvfs mount.</summary>
    private static string DrvfsRoot(MountEntry mount) =>
        !mount.IsDrvfs ? string.Empty : mount.Option("path") is { Length: > 0 } path ? path : mount.Source;

    private static string JoinWindows(string root, string rest) => rest.Length == 0 ? root : root.TrimEnd('\\') + "\\" + rest;

    /// <summary>E9.S1 review round m2: a drvfs mount of a share back to this machine (<c>path=\\localhost\C$</c>) reaches the drive
    /// under another name — refused as the Windows side refuses that share.</summary>
    private static Placement NotALoopbackShare(Placement placement) =>
        placement is Placement.Placed { WindowsSpelling: var windows } && WindowsShares.Alias(windows) is { Length: > 0 } alias
            ? new Placement.Refused(BaseFolderRule.Shape, $"it lies on a drvfs mount of {windows}: {alias}")
            : placement;

    /// <summary>The E9 network-base fix's own review, 1: a mapped NETWORK drive is judged by the share it maps to — the share-alias rule
    /// (the distribution's own files, this machine, an administrative share) through the mapping, and a network drive whose share
    /// cannot be read is refused. Empty for a local drive and for a share of its own.</summary>
    internal static string MappedShareProblem(string given, Func<string, Files.DriveMapping> mappingOf) =>
        Files.NetworkPaths.DriveOf(given) is { Length: > 0 } drive && mappingOf(drive) is { Remote: true } mapping
            ? MappingProblem(given, drive, mapping)
            : string.Empty;

    private static string MappingProblem(string given, string drive, Files.DriveMapping mapping) =>
        mapping.Root.Length == 0 ? $"{given} is on the network drive {drive}, whose share cannot be read; name the share itself"
        : WindowsShares.Alias(mapping.Root.TrimEnd('\\') + given[drive.Length..]) is { Length: > 0 } alias ? $"{given} is on {drive}, mapped to {mapping.Root}: {alias}"
        : string.Empty;

    private static Placement PlaceOnWindows(IFileSystem files, string given) =>
        given.StartsWith('/') ? new Placement.Refused(BaseFolderRule.Shape, $"{given} is a Linux path; the Windows side names a drive folder (V:\\…) or a share (\\\\server\\share\\…)")
        : (WindowsShares.Alias(given) is { Length: > 0 } alias ? alias : MappedShareProblem(given, Files.NetworkPaths.MappingOnThisMachine)) is { Length: > 0 } refused
            ? new Placement.Refused(BaseFolderRule.Shape, refused)
        : PlacedOnWindows(files.ResolvePath(given) is RealPathResult.Resolved real ? real.Path.TrimEnd('\\') : given.TrimEnd('\\'), given.TrimEnd('\\'));

    private static Placement.Placed PlacedOnWindows(string real, string spelled) =>
        new(RootedDrive(real), RootedDrive(real), RootedDrive(spelled), WindowsMount(real), string.Empty, []);

    /// <summary>A drive's root keeps its separator (<c>V:\</c>, never <c>V:</c>, which names the drive's current folder).</summary>
    private static string RootedDrive(string path) => path.Length == 2 && path[1] == ':' ? path + "\\" : path;

    /// <summary>The drive's kind and format, or the share — the Windows side's "mount".</summary>
    private static MountView WindowsMount(string folder)
    {
        if (folder.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var share = string.Join('\\', folder[2..].Replace('/', '\\').Split('\\').Take(2));
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
            return new MountView(BaseMountReport.Unknown, false, false);
        }
    }
}
