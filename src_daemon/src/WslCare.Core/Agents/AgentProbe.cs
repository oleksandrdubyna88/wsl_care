using WslCare.Core.Actions;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.Core.Status;

namespace WslCare.Core.Agents;

/// <summary>A probed folder: its size as the walk measured it, and why it could not be a manual agent's (empty = it could).</summary>
public sealed record AgentProbeFolder(AgentFolderReport Folder, string Refusal);

/// <summary>The answer of <c>agents probe &lt;path&gt; --json</c> (plan §15q D4).</summary>
/// <param name="Usable">The path is a regular file this user may start (looked at, never started).</param>
/// <param name="TrackedAs">The catalogue agent whose binary has this name — then it is already tracked; empty otherwise.</param>
/// <param name="Suggested">The <c>aiAgents.extra</c> entry the extension would add: the folders that exist AND pass the rules;
/// absent when there is none to add.</param>
public sealed record AgentProbeReport(
    int SchemaVersion,
    string Side,
    string Path,
    bool Link,
    bool Usable,
    string Reason,
    string Name,
    string TrackedAs,
    IReadOnlyList<AgentProbeFolder> DataFolders,
    ExtraAgent? Suggested);

/// <summary>
/// <c>agents probe &lt;path&gt;</c> (plan §15q D4): what a CLI the person picked is, as THIS user sees it — never root (the CLI
/// refuses that, naming uid 0) — with nothing executed and no byte of it read: the file is stat-ed (a link is followed to say
/// whether it can be started, and reported as a link), a name is derived from its FILE NAME, and the conventional data folders
/// of that name under the home are listed with their sizes and judged by the same rules a manual agent's folders are
/// (<see cref="ExtraAgentRules"/>). The answer is what the extension's host modal shows before anything is saved.
/// </summary>
public static class AgentProbe
{
    /// <summary>Where a CLI named <c>n</c> conventionally keeps its data, under the home.</summary>
    public static readonly IReadOnlyList<string[]> Conventional = [["." + "{0}"], [".config", "{0}"], [".local", "share", "{0}"], [".cache", "{0}"]];

    public static AgentProbeReport Probe(LinuxHostPaths paths, IFileSystem files, string path, TimeProvider clock, CancellationToken cancellationToken)
    {
        var local = paths.DistroPath(path);
        var name = NameOf(path);
        var link = files.ReadLink(local) is LinkReadResult.Target;
        var (usable, reason) = Startable(files, local, path);
        var tracked = AgentCatalogue.Agents.FirstOrDefault(a => a.Binaries.Contains(name, StringComparer.Ordinal))?.Name ?? string.Empty;
        var folders = usable && name.Length > 0 ? Folders(paths, files, name, clock, cancellationToken) : [];
        var accepted = folders.Where(f => f.Refusal.Length == 0).Select(f => f.Spelt).ToList();
        var suggested = tracked.Length == 0 && accepted.Count > 0 ? new ExtraAgent(path, ExtraAgentShape.Wsl, name, accepted, string.Empty) : null;
        return new AgentProbeReport(SchemaVersion.Current, "wsl", path, link, usable, NameReason(reason, name), name, tracked, [.. folders.Select(f => f.Probed)], suggested);
    }

    /// <summary>A name from the FILE NAME: the characters a manual agent's name allows, at most its length; empty when none.</summary>
    public static string NameOf(string path)
    {
        var file = path[(path.LastIndexOf('/') + 1)..];
        var kept = new string([.. file.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+' or '-')]).TrimStart('.', '_', '+', '-');
        return kept.Length > ExtraAgentShape.MaxNameLength ? kept[..ExtraAgentShape.MaxNameLength] : kept;
    }

    private static string NameReason(string reason, string name) =>
        reason.Length > 0 ? reason : name.Length == 0 ? "no name can be derived from the file name" : string.Empty;

    private static (bool Usable, string Reason) Startable(IFileSystem files, string local, string path) =>
        !files.FileExists(local) ? (false, $"{path} is not a file that exists (a folder, or nothing)")
        : !ExecutableResolver.IsStartable(local, windows: false) ? (false, $"{path} has no execute bit: this user cannot start it")
        : (true, string.Empty);

    private sealed record Candidate(string Spelt, AgentProbeFolder Probed, string Refusal);

    private static IReadOnlyList<Candidate> Folders(LinuxHostPaths paths, IFileSystem files, string name, TimeProvider clock, CancellationToken cancellationToken)
    {
        var home = paths.Home;
        var existing = Conventional.Select(c => paths.Rules.Join(home, [.. c.Select(s => string.Format(System.Globalization.CultureInfo.InvariantCulture, s, name))]))
            .Where(files.DirectoryExists).ToList();
        var agent = new ExtraAgent("/probe", ExtraAgentShape.Wsl, name, existing, string.Empty);
        var judged = ExtraAgentRules.Judge(paths, files, [.. existing.Select(f => agent with { DataFolders = [Distro(paths, f)] })], ExtraAgentRules.CleanupRoots(ActionRegistry.Product, home, paths.Rules));
        var walk = new AgentWalk(files, clock);
        return [.. existing.Zip(judged).Select(pair => Measured(walk, pair.First, pair.Second, paths, cancellationToken))];
    }

    private static Candidate Measured(AgentWalk walk, string local, ExtraJudgement judged, LinuxHostPaths paths, CancellationToken cancellationToken)
    {
        var target = new AgentTarget(ExtraAgents.EntryOf(judged.Agent), [local], string.Empty, judged.Refusal);
        var size = walk.Measure([target], AgentWalk.MeasureNowBudget, withNames: false, cancellationToken).Agents[0].Folders[0];
        return new Candidate(Distro(paths, local), new AgentProbeFolder(AgentsReports.FolderOf(size with { Path = Distro(paths, local) }), judged.Refusal), judged.Refusal);
    }

    /// <summary>The path as the distro names it (what the extension saves), from one this process sees under the sandbox root.</summary>
    private static string Distro(LinuxHostPaths paths, string local)
    {
        var root = paths.DistroPath("/");
        return local.StartsWith(root, StringComparison.Ordinal) && root.Length > 1 ? "/" + local[root.Length..].TrimStart('/', '\\').Replace('\\', '/') : local;
    }
}
