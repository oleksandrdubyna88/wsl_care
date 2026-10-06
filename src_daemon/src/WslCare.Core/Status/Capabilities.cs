namespace WslCare.Core.Status;

/// <summary>
/// What this build can do beyond the verbs every <c>0.1.0</c> daemon answers — named in <c>status --json</c>'s
/// <c>capabilities</c> (plan §15f #3, §15j M5). The extension acts on these, never on a version number: a daemon may act
/// only if it ADVERTISES what the action needs, and the version only supplies the message ("update the daemon").
/// </summary>
/// <remarks>A capability is added in the story that delivers it and never removed (removing one is a breaking change,
/// plan §6). E6.S0 delivers the four below; <c>act.detach</c>, <c>act.onlyStdin</c> and <c>act.stop</c> arrive with
/// E6.S1.</remarks>
public static class Capabilities
{
    /// <summary><c>act A4 --preview --json</c> carries <c>shown</c> — EVERY name the preview selected (§15j B1).</summary>
    public const string ActShownList = "act.shownList";

    /// <summary><c>runs show &lt;runId&gt; --json</c> answers one run's state and full detail (§15j M3).</summary>
    public const string RunsShow = "runs.show";

    /// <summary><c>status --json</c> carries the <c>running</c> block (§15j M3).</summary>
    public const string RunningBlock = "running.block";

    /// <summary><c>logs</c> / <c>runs</c> take <c>--from &lt;RFC3339&gt; --to &lt;RFC3339&gt;</c> beside the UTC-day periods (§15j M7).</summary>
    public const string LogsInstantRange = "logs.instantRange";

    /// <summary><c>act … --confirm --detach</c> and <c>collect --detach</c>: a run started in its own template unit, answered
    /// <c>accepted</c> at once (§15j B2, M9; E6.S1).</summary>
    public const string ActDetach = "act.detach";

    /// <summary><c>act … --only -</c>: A4's shown list on stdin, capped at 1 MiB and 10 s (§15j M2; E6.S1).</summary>
    public const string ActOnlyStdin = "act.onlyStdin";

    /// <summary><c>act --stop &lt;runId&gt;</c>: a wedged run hosted by one of the units stopped through systemd (§15j M4; E6.S1).</summary>
    public const string ActStop = "act.stop";

    /// <summary><c>config get</c> / <c>config set</c> obey <c>contracts/config-keys.json</c> (plan §15q D5, E7.S0): every text and
    /// list key closed, the user layer read owner-checked, a value a root run does not take answered as a notice.</summary>
    public const string ConfigContract = "config.contract";

    /// <summary><c>agents list [--measure] --json</c> answers the catalogue agents found here and their folders (plan §15q E7.S1).</summary>
    public const string AgentsList = "agents.list";

    /// <summary><c>agents probe &lt;path&gt; --json</c> answers what a picked CLI is, as the user (plan §15q D4, E7.S2).</summary>
    public const string AgentsProbe = "agents.probe";

    /// <summary><c>config set aiAgents.extra -</c> takes the manual AI agents from stdin, each judged against the disk; their folders
    /// are protected and walked (plan §15q R2, E7.S2).</summary>
    public const string ConfigAgentsExtra = "config.agentsExtra";

    /// <summary>Every capability this build has, in the order they were added.</summary>
    public static IReadOnlyList<string> All { get; } = [ActShownList, RunsShow, RunningBlock, LogsInstantRange, ActDetach, ActOnlyStdin, ActStop, ConfigContract, AgentsList, AgentsProbe, ConfigAgentsExtra];
}
