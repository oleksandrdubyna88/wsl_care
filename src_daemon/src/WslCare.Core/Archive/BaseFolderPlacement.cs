using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Archive;

/// <summary>
/// Where a base lies before any folder is looked at (plan §15r D7, E9.S0 review round S3, S4): in the distro, placed by the mount
/// table — a Windows drive path at its drvfs mount, a bind mount at the folder it really is, the distribution's own disk known by
/// its DEVICE; on Windows, a share refused when it is this machine or an administrative share.
/// </summary>
public static partial class BaseFolderRules
{
    /// <summary>Where a base was placed, or the rule that refused it before any folder was looked at.</summary>
    private abstract record Placement
    {
        private Placement()
        {
        }

        /// <param name="Folder">The base as this side spells it.</param>
        /// <param name="OnDisk">The same as this process opens it (under the sandbox root when sandboxed).</param>
        /// <param name="WindowsSpelling">On a drvfs mount, the folder as Windows spells it (<c>C:\Users\me\x</c>); empty otherwise.</param>
        /// <param name="Notes">What the placement itself found (a bind mount followed).</param>
        public sealed record Placed(string Folder, string OnDisk, MountView Mount, string WindowsSpelling, IReadOnlyList<string> Notes) : Placement;

        public sealed record Refused(string Rule, string Why) : Placement;
    }

    /// <summary>The filesystem as the judgement needs it.</summary>
    private sealed record MountView(BaseMountReport Report, bool Drvfs, bool IsDistroDisk);

    private static Placement Place(IHostPaths paths, IFileSystem files, string given) =>
        ShapeProblem(given) is { Length: > 0 } shape ? new Placement.Refused(BaseFolderRule.Shape, shape)
        : paths is LinuxHostPaths linux ? PlaceInDistro(linux, files, given)
        : PlaceOnWindows(given);

    private static string ShapeProblem(string given) =>
        given.Length == 0 ? "no folder was given"
        : new Config.TextRule.AbsolutePathOrEmpty().Problem(given) is { Length: > 0 } problem ? $"{given} is {problem}"
        : string.Empty;

    private static Placement PlaceInDistro(LinuxHostPaths paths, IFileSystem files, string given) =>
        given.StartsWith(@"\\", StringComparison.Ordinal)
            ? new Placement.Refused(BaseFolderRule.Shape, $"{given} is a Windows share; inside the distribution name the folder where the share is mounted (a drvfs mount of it)")
            : ProcText.Read(files, paths.Rules.Join(paths.ProcRoot, "self", "mountinfo")) switch
            {
                Reading<string>.Available table => InTable(paths, given, MountTable.Parse(table.Value)),
                var unreadable => new Placement.Refused(BaseFolderRule.MountUnreadable, $"the mount table could not be read ({unreadable.ReasonOrEmpty}), so the filesystem the base lies on is unknown"),
            };

    private static Placement InTable(LinuxHostPaths paths, string given, IReadOnlyList<MountEntry> mounts) =>
        given.StartsWith('/')
            ? Placed(paths, Trimmed(given), mounts)
            : DriveMount(mounts, given[0]) is { } drive
                ? Placed(paths, Trimmed(drive.MountPoint.TrimEnd('/') + "/" + given[3..].Replace('\\', '/')), mounts)
                : new Placement.Refused(BaseFolderRule.DriveNotMounted, $"drive {char.ToUpperInvariant(given[0])}: is not mounted in this distribution (no drvfs mount of the whole drive in the mount table); mount it, or choose a folder the distribution sees");

    private static MountEntry? DriveMount(IReadOnlyList<MountEntry> mounts, char letter) => mounts.LastOrDefault(m => MountTable.IsWholeDrive(m, letter));

    private static Placement Placed(LinuxHostPaths paths, string folder, IReadOnlyList<MountEntry> mounts) => MountTable.Holding(mounts, folder) switch
    {
        null => new Placement.Refused(BaseFolderRule.MountUnreadable, $"the mount table names no filesystem holding {folder}, so what it lies on is unknown"),
        var mount when IsCanonical(mount, mounts) => new Placement.Placed(folder, paths.DistroPath(folder), View(mount, mounts), WindowsSpelling(mount, folder), []),
        var alias => ThroughAlias(paths, folder, alias, mounts),
    };

    /// <summary>The one mount a device's folders are judged under: the filesystem's whole mount (root <c>/</c>) — the distribution's
    /// own <c>/</c> when it is that disk, else the shortest mount point; none when the device is mounted whole nowhere.</summary>
    private static MountEntry? CanonicalOf(MountEntry mount, IReadOnlyList<MountEntry> mounts) =>
        mounts.Where(m => m.Root == "/" && m.Major == mount.Major && m.Minor == mount.Minor)
            .OrderBy(m => m.MountPoint == "/" ? 0 : 1).ThenBy(m => m.MountPoint.Length).ThenBy(m => m.MountPoint, StringComparer.Ordinal)
            .FirstOrDefault();

    private static bool IsCanonical(MountEntry mount, IReadOnlyList<MountEntry> mounts) =>
        mount.Root == "/" && CanonicalOf(mount, mounts) is { } canonical && canonical.MountPoint == mount.MountPoint;

    /// <summary>E9.S0 review round S4: a bind mount — or a second mount of a filesystem — shows its folders under a new name:
    /// <c>/mnt/x</c> may BE <c>~/.claude</c>. The base is judged at the folder it really is, under the device's canonical mount. A
    /// device mounted whole nowhere is refused — where the folder really lies is then unknown.</summary>
    private static Placement ThroughAlias(LinuxHostPaths paths, string folder, MountEntry alias, IReadOnlyList<MountEntry> mounts)
    {
        if (CanonicalOf(alias, mounts) is not { } canonical)
        {
            return new Placement.Refused(BaseFolderRule.LinkOnTheWay, $"{folder} lies on a bind mount of {alias.Root} (device {alias.Major}:{alias.Minor}) whose filesystem is mounted whole nowhere here, so where it really lies is unknown");
        }

        var real = Trimmed(canonical.MountPoint.TrimEnd('/') + alias.Root.TrimEnd('/') + folder[alias.MountPoint.TrimEnd('/').Length..]);
        return MountTable.Holding(mounts, real) is { } holding && IsCanonical(holding, mounts)
            ? new Placement.Placed(real, paths.DistroPath(real), View(holding, mounts), WindowsSpelling(holding, real), [$"{folder} is {alias.Root} of the filesystem mounted at {canonical.MountPoint} (a bind or second mount at {alias.MountPoint}): judged where it really lies, {real}"])
            : new Placement.Refused(BaseFolderRule.LinkOnTheWay, $"{folder} is {real} through a bind mount, and {real} is itself reached through another: refused rather than followed further");
    }

    /// <summary>The distribution's own disk is its DEVICE (or its source), not the mount point <c>/</c>: a second mount of it is the
    /// same disk (E9.S0 review round S4).</summary>
    private static MountView View(MountEntry mount, IReadOnlyList<MountEntry> mounts) =>
        new(new BaseMountReport(mount.MountPoint, mount.Type, mount.Source), mount.IsDrvfs, mounts.LastOrDefault(m => m.MountPoint == "/") is { } root && SameDisk(mount, root));

    private static bool SameDisk(MountEntry mount, MountEntry root) =>
        (mount.Major == root.Major && mount.Minor == root.Minor) || (root.Source.StartsWith("/dev/", StringComparison.Ordinal) && mount.Source == root.Source);

    /// <summary>A drvfs folder as Windows spells it: the mounted drive or share (<c>path=</c>, else the source) and the rest of the
    /// path; empty for any other filesystem.</summary>
    private static string WindowsSpelling(MountEntry mount, string folder)
    {
        var root = mount.Option("path") is { Length: > 0 } path ? path : mount.Source;
        var rest = folder[mount.MountPoint.TrimEnd('/').Length..].Trim('/').Replace('/', '\\');
        return !mount.IsDrvfs || root.Length < 2 ? string.Empty : rest.Length == 0 ? root : root.TrimEnd('\\') + "\\" + rest;
    }

    private static Placement PlaceOnWindows(string given) =>
        given.StartsWith('/') ? new Placement.Refused(BaseFolderRule.Shape, $"{given} is a Linux path; the Windows side names a drive folder (V:\\…) or a share (\\\\server\\share\\…)")
        : WindowsShares.Alias(given) is { Length: > 0 } alias ? new Placement.Refused(BaseFolderRule.Shape, alias)
        : new Placement.Placed(given.TrimEnd('\\'), given.TrimEnd('\\'), WindowsMount(given), string.Empty, []);

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
            return new MountView(BaseMountReport.Unknown, false, false);
        }
    }
}

/// <summary>
/// E9.S0 review round S3: the shares that reach this machine's own folders under another spelling — the distribution's files
/// (<c>\\wsl$</c>, <c>\\wsl.localhost</c>), a loopback name or this machine's name, an administrative share (<c>C$</c>,
/// <c>ADMIN$</c>, <c>IPC$</c>). The overlap rule compares spellings and identities of THIS machine's folders; a share back to them
/// would slip past both, so it is refused by its name.
/// </summary>
public static class WindowsShares
{
    private static readonly string[] DistroServers = ["wsl$", "wsl.localhost"];

    private static readonly string[] Loopback = ["localhost", "127.0.0.1", "::1", "[::1]", "0--1.ipv6-literal.net", "."];

    /// <summary>Why <paramref name="given"/> is a share this rule refuses; empty when it is none.</summary>
    public static string Alias(string given) =>
        given.StartsWith(@"\\", StringComparison.Ordinal) && given[2..].Split('\\') is [var server, var share, ..] ? Why(given, server, share) : string.Empty;

    private static string Why(string given, string server, string share) =>
        DistroServers.Contains(server, StringComparer.OrdinalIgnoreCase) ? $"{given} is the distribution's own files; name the folder inside the distribution, where its own process judges it"
        : IsThisMachine(server) ? $"{given} names this machine ({server}); name the folder by its drive, so it is judged as the folder it is"
        : IsAdministrative(share) ? $"{given} is an administrative share ({share}), a second spelling of a whole drive; name the folder by a share of its own or by its drive"
        : string.Empty;

    private static bool IsThisMachine(string server) =>
        Loopback.Contains(server, StringComparer.OrdinalIgnoreCase) || server.StartsWith("127.", StringComparison.Ordinal) || IsMachineName(server);

    /// <summary>This machine's name, bare or with a domain after it.</summary>
    private static bool IsMachineName(string server) =>
        string.Equals(server, Environment.MachineName, StringComparison.OrdinalIgnoreCase) || server.StartsWith(Environment.MachineName + ".", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] AdministrativeShares = ["ADMIN$", "IPC$"];

    private static bool IsAdministrative(string share) => IsDriveShare(share) || AdministrativeShares.Contains(share, StringComparer.OrdinalIgnoreCase);

    /// <summary><c>C$</c>: a whole drive, shared by Windows itself.</summary>
    private static bool IsDriveShare(string share) => share.Length == 2 && char.IsAsciiLetter(share[0]) && share[1] == '$';
}
