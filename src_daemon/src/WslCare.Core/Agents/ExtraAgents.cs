using WslCare.Core.Actions;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Agents;

/// <summary>
/// The manual AI agents of <c>aiAgents.extra</c> as discovery answers them (plan §15q D4, R2): one presence per entry of THIS
/// side, detected <see cref="Manual"/>, judged against the disk NOW (<see cref="ExtraAgentRules"/> — never trusted because it
/// validated at <c>config set</c>). A refused entry stays in the answer with its refusal and is not walked; its folders stay
/// protected all the same (review B2). The entry's <c>cli</c> is never looked at: not as root, not by the user's own run.
/// </summary>
public static class ExtraAgents
{
    public const string Manual = "manual";

    public const string WindowsRulesLater = "not walked: a Windows agent's folder rules arrive with E7.S5b; protected as spelt";

    public const string NotAsked = "not asked: a manual agent's CLI is never looked at by the daemon";

    /// <summary>Every manual agent of this side; <paramref name="registry"/> gives the cleanup folders an entry may not overlap.</summary>
    public static IReadOnlyList<AgentPresence> Discover(IHostPaths paths, IFileSystem files, EffectiveConfig config, ActionRegistry registry)
    {
        var extras = config.Agents(ConfigKeys.AiAgents.Extra);
        return paths switch
        {
            LinuxHostPaths linux => [.. ExtraAgentRules.Judge(linux, files, [.. extras.Where(e => e.Side == ExtraAgentShape.Wsl)], ExtraAgentRules.CleanupRoots(registry, linux.Home, linux.Rules)).Select(Presence)],
            _ => [.. extras.Where(e => e.Side == ExtraAgentShape.Windows).Select(e => Presence(new ExtraJudgement(e, e.DataFolders, WindowsRulesLater)))],
        };
    }

    /// <summary>The catalogue entry a manual agent walks under: no binaries, no packages, nothing it names never-entered but
    /// <c>memory</c> (always, H2), and its own session glob — under its FIRST data folder — when it gave one.</summary>
    public static AgentEntry EntryOf(ExtraAgent agent) =>
        new(
            agent.Id,
            agent.Name,
            [],
            [],
            [],
            [],
            [],
            [],
            agent.SessionGlob.Length == 0 ? null : new AgentSessionLayout(string.Empty, string.Empty, agent.SessionGlob, "aiAgents.extra (the person's own layout)"),
            string.Empty,
            false);

    private static AgentPresence Presence(ExtraJudgement judged) =>
        new(EntryOf(judged.Agent), [Manual], [], string.Empty, NotAsked, judged.Folders, SessionsUnder(judged))
        {
            Refusal = judged.Refusal,
        };

    private static string SessionsUnder(ExtraJudgement judged) =>
        judged.Accepted && judged.Agent.SessionGlob.Length > 0 && judged.Folders.Count > 0 ? judged.Folders[0] : string.Empty;
}
