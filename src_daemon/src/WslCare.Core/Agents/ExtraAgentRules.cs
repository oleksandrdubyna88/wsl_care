using WslCare.Core.Actions;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Agents;

/// <summary>A manual agent judged against the disk: its data folders as this process sees them, and why it may not be walked
/// (empty = accepted). A refused entry leaves the WALK only — its folders stay protected (plan §15q R2, review B2).</summary>
public sealed record ExtraJudgement(ExtraAgent Agent, IReadOnlyList<string> Folders, string Refusal)
{
    public bool Accepted => Refusal.Length == 0;
}

/// <summary>A folder a manual agent's folder may not overlap, and whose it is ("A12's cleanup folder", "Codex's folder").</summary>
public sealed record ForbiddenFolder(string Path, string Whose);

/// <summary>
/// Plan §15q R2.1 — the filesystem rules of a manual agent's data folder, applied at <c>config set</c> (as the user) AND again at
/// every root read (never trusted because it validated once): its REAL path lies strictly inside the target user's real home,
/// on the home's own filesystem, and is not equal to, inside or containing <c>~/git</c>, Claude's temporary folders, a catalogue
/// agent's folder ("already tracked"), any folder a cleanup action cleans (read from the action registry — review M8), or the
/// product's own folders (review M9). Only the distro's entries are judged here; a Windows entry is the Windows binary's.
/// </summary>
public static class ExtraAgentRules
{
    public const string WindowsEntry = "a Windows agent: the Windows binary walks it (E7.S5b); protected here as spelt";

    private static readonly PathRules Rules = PathRules.ForThisOs;

    /// <summary>Every entry of <paramref name="extras"/> judged; <paramref name="cleanupRoots"/> are the registry's.</summary>
    public static IReadOnlyList<ExtraJudgement> Judge(LinuxHostPaths paths, IFileSystem files, IReadOnlyList<ExtraAgent> extras, IReadOnlyList<ForbiddenFolder> cleanupRoots)
    {
        var home = RealOrFull(files, paths.Home);
        var forbidden = Forbidden(paths, cleanupRoots).Select(f => f with { Path = RealOrFull(files, f.Path) }).ToList();
        return [.. extras.Select(e => JudgeOne(paths, files, e, home, forbidden))];
    }

    /// <summary>The cleanup folders the registry's actions declare, under <paramref name="home"/>, named by their action.</summary>
    public static IReadOnlyList<ForbiddenFolder> CleanupRoots(ActionRegistry registry, string home, PathRules rules) =>
        [.. registry.Actions.SelectMany(a => a.HomeRoots.Select(r => new ForbiddenFolder(r.Under(home, rules), $"{a.Id.Text}'s cleanup folder {r.Display}")))];

    private static ExtraJudgement JudgeOne(LinuxHostPaths paths, IFileSystem files, ExtraAgent agent, string home, IReadOnlyList<ForbiddenFolder> forbidden)
    {
        if (agent.Side != ExtraAgentShape.Wsl)
        {
            return new ExtraJudgement(agent, [], WindowsEntry);
        }

        var folders = agent.DataFolders.Select(paths.DistroPath).ToList();
        var refusal = agent.DataFolders.Zip(folders)
            .Select(pair => FolderRefusal(files, pair.First, pair.Second, home, forbidden))
            .FirstOrDefault(r => r.Length > 0) ?? string.Empty;
        return new ExtraJudgement(agent, folders, refusal);
    }

    /// <summary>Why <paramref name="spelt"/> (seen here as <paramref name="path"/>) may not be walked; empty when it may.</summary>
    public static string FolderRefusal(IFileSystem files, string spelt, string path, string home, IReadOnlyList<ForbiddenFolder> forbidden) =>
        files.ResolvePath(path) switch
        {
            RealPathResult.Unresolvable u => $"{spelt} cannot be resolved ({u.Reason} at {u.Component})",
            RealPathResult.Resolved r when !files.DirectoryExists(r.Path) => $"{spelt} does not exist or is not a folder",
            RealPathResult.Resolved r => PlaceRefusal(files, spelt, r.Path, home, forbidden),
            _ => throw new System.Diagnostics.UnreachableException("RealPathResult is a closed set"),
        };

    private static string PlaceRefusal(IFileSystem files, string spelt, string real, string home, IReadOnlyList<ForbiddenFolder> forbidden) =>
        !Rules.IsStrictlyUnder(real, home) ? $"{spelt} is not inside the home (the home itself and anything outside it are refused)"
        : files.DeviceOf(real) is not { } device || files.DeviceOf(home) != device ? $"{spelt} is on another filesystem than the home (a mount, /mnt/c over 9p) — refused"
        : forbidden.FirstOrDefault(f => Overlaps(real, f.Path)) is { } clash ? $"{spelt} overlaps {clash.Whose} — refused"
        : string.Empty;

    /// <summary>Equal, inside, or containing.</summary>
    public static bool Overlaps(string a, string b) => Rules.IsSameOrUnder(a, b) || Rules.IsStrictlyUnder(b, a);

    private static IEnumerable<ForbiddenFolder> Forbidden(LinuxHostPaths paths, IReadOnlyList<ForbiddenFolder> cleanupRoots) =>
    [
        .. paths.GitRoots.Select(g => new ForbiddenFolder(g, "~/git, under which nothing is ever touched")),
        .. paths.ClaudeTempRoots.Select(t => new ForbiddenFolder(t, "Claude's temporary folder")),
        .. AgentCatalogue.Agents.SelectMany(a => a.Linux.Select(f => new ForbiddenFolder(AgentCatalogue.LinuxFolder(f, paths.Home), $"{a.Name}'s folder {f} (already tracked)"))),
        .. cleanupRoots,
        .. ProductFolders(paths).Select(p => new ForbiddenFolder(p, $"wsl-care's own folder {p}")),
    ];

    /// <summary>The product's own configuration, state and log folders (review M9: an extra there would lock <c>config set</c>).</summary>
    public static IReadOnlyList<string> ProductFolders(IHostPaths paths) =>
        [Path.GetDirectoryName(paths.UserConfigFile) ?? paths.UserConfigFile, paths.StateDirectory, paths.LogDirectory, paths.UserLogDirectory];

    private static string RealOrFull(IFileSystem files, string path) =>
        files.ResolvePath(path) is RealPathResult.Resolved r ? r.Path : Path.GetFullPath(path);
}
