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

    /// <summary>Every capability this build has, in the order they were added.</summary>
    public static IReadOnlyList<string> All { get; } = [ActShownList, RunsShow, RunningBlock, LogsInstantRange];
}
