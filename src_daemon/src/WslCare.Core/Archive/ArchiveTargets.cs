using WslCare.Core.Actions;
using WslCare.Core.Agents;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Archive;

/// <summary>One agent the selection lists: its entry, the folder its layout starts in, and why it is not listed (empty = it is).</summary>
public sealed record ArchiveTarget(AgentEntry Entry, string Under, string Refusal);

/// <summary>
/// The agents the archive is asked to look at (<c>archive.agents</c>, plan §15r): the catalogue's agents with an archive block, and
/// — E9.S0 review round decision (b), off by default — manual agents of <c>aiAgents.extra</c> named <c>manual:&lt;name&gt;</c> that
/// carry a session glob, judged by the manual agent's own folder rules (<see cref="ExtraAgentRules"/>) at every selection. A
/// manual agent is archived by its own glob under its first data folder, as one session = one file with no companions; it names
/// nothing that never moves but <c>memory</c> (always), and its own deletion is not known.
/// </summary>
public static class ArchiveTargets
{
    /// <summary>The archive block every manual agent carries.</summary>
    public static AgentArchiveBlock ManualBlock { get; } = new(
        [new ArchiveUnit(ArchiveUnitKinds.Session, string.Empty, [])],
        [],
        new AgentRetention(RetentionSources.None, 0, "a manual agent: its own deletion is not known"));

    /// <summary>The targets of <paramref name="config"/>'s <c>archive.agents</c> on this side, catalogue first, each manual one in
    /// the order the list names it; only <paramref name="onlyAgent"/> when it is given.</summary>
    public static IReadOnlyList<ArchiveTarget> Of(IHostPaths paths, IFileSystem files, EffectiveConfig config, string onlyAgent)
    {
        var enabled = config.TextList(ConfigKeys.Archive.Agents).Where(id => onlyAgent.Length == 0 || id == onlyAgent).ToList();
        var catalogue = AgentCatalogue.Agents.Where(a => a.Archive is not null && enabled.Contains(a.Id, StringComparer.Ordinal))
            .Select(a => new ArchiveTarget(a, AgentDiscovery.SessionsUnderOf(paths, a), string.Empty));
        var extras = config.Agents(ConfigKeys.AiAgents.Extra);
        var manual = enabled.Where(id => id.StartsWith(ExtraAgent.IdPrefix, StringComparison.Ordinal)).Select(id => Manual(paths, files, extras, id));
        return [.. catalogue, .. manual];
    }

    private static ArchiveTarget Manual(IHostPaths paths, IFileSystem files, IReadOnlyList<ExtraAgent> extras, string id) =>
        extras.FirstOrDefault(e => e.Id == id) switch
        {
            null => Refused(Placeholder(id), $"{id} is in archive.agents, but aiAgents.extra holds no agent of that name"),
            { SessionGlob.Length: 0 } agent => Refused(EntryOf(agent), $"{agent.Name} names no sessionGlob in aiAgents.extra, so what one of its sessions is was never said; it is not archived"),
            var agent => Judged(paths, files, agent),
        };

    /// <summary>A distro agent judged by its folder rules, as at every root read; a Windows one waits for the Windows side's rules.</summary>
    private static ArchiveTarget Judged(IHostPaths paths, IFileSystem files, ExtraAgent agent)
    {
        if (paths is not LinuxHostPaths linux || agent.Side != ExtraAgentShape.Wsl)
        {
            return Refused(EntryOf(agent), $"{agent.Name} is a {agent.Side} agent; this side archives only the manual agents whose folders its own rules judge (the Windows side's are E7.S5b's)");
        }

        var judged = ExtraAgentRules.Judge(linux, files, [agent], ExtraAgentRules.CleanupRoots(ActionRegistry.Product, linux.Home, linux.Rules)).Single();
        return judged.Accepted && judged.Folders.Count > 0
            ? new ArchiveTarget(EntryOf(agent), judged.Folders[0], string.Empty)
            : Refused(EntryOf(agent), judged.Refusal);
    }

    private static AgentEntry EntryOf(ExtraAgent agent) => ExtraAgents.EntryOf(agent) with { Archive = ManualBlock };

    /// <summary>An entry for a listed manual id aiAgents.extra does not hold — answered, never walked.</summary>
    private static AgentEntry Placeholder(string id) => new(id, id, [], [], [], [], [], [], null, string.Empty, false) { Archive = ManualBlock };

    private static ArchiveTarget Refused(AgentEntry entry, string why) => new(entry, string.Empty, why);
}
