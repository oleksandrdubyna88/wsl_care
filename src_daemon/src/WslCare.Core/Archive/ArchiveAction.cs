using System.Globalization;

using WslCare.Core.Actions;
using WslCare.Core.Config;
using WslCare.Core.Hosting;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;

namespace WslCare.Core.Archive;

/// <summary>
/// A13 (plan §15r D1, D8, E9.S4): move aged AI-agent sessions into the archive — root's timer decides WHEN, the TARGET USER's
/// own process does every byte: <c>runuser -u &lt;user&gt; -- &lt;the installed wsl-care&gt; archive …</c>, the product's own
/// root-owned binary through closed self-invocation templates. Root never opens a session file nor the base.
/// </summary>
/// <remarks>
/// <para><b>Preview</b> = the child's own <c>archive preview --json</c> (what would move, and how many archived sessions wait for their
/// removal), in AGGREGATES per agent — the run detail is world-readable and names no session (§15q R9). A skip with the
/// reason: no base (<c>archive.baseFolder</c> empty), a recorded child still alive (risk consult 9/9.4 #1), no time left in a
/// timer run; a refusal: a <c>runuser</c> PAM stack that names <c>pam_systemd</c> (#2), a product binary that is not root's alone.</para>
/// <para><b>Trigger</b>: a session due or a removal due. <b>Idle</b>: the timer waits for it, unless the oldest due session is within
/// <c>archive.urgentWithinDays</c> of its agent's own deletion.</para>
/// <para><b>Run</b>: the short reach child first (the side's lock, the base within <c>archive.reachabilitySeconds</c>), then the
/// STREAMED run child under a per-request ceiling taken from the run limit's slack (D8), its launcher and worker recorded while they
/// live; its answer judged (<see cref="ArchiveChildAnswers"/>) before one count is taken. "Freed" is what the removals took from the
/// agents' folders — moved, not deleted.</para>
/// </remarks>
public sealed class ArchiveAction : ICleanupAction
{
    public const string NoArchive = "no archive configured: archive.baseFolder is empty";

    private const string Fact = "dueSessions";
    private const string RemovalsFact = "removalsDue";

    public ActionId Id { get; } = ActionId.Find("A13")!;

    public string Summary => "move aged AI-agent sessions into the archive (archive.baseFolder) as the target user — the product's own binary, never root";

    public CommandScope Scope => CommandScope.User;

    public IdleRule Idle => IdleRule.TimerOnly;

    public IReadOnlyList<HostSide> Sides { get; } = [HostSide.Wsl];

    public IReadOnlyList<CommandTemplate> Commands { get; } = [ArchiveChildren.Preview, ArchiveChildren.Reach, ArchiveChildren.Run];

    public async Task<ActionPreview> PreviewAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken)
    {
        if ((ArchiveGates.Before(context, commands, cancellationToken) ?? NoTimeLeft(context)) is { } gated)
        {
            return gated;
        }

        var preview = await ArchiveGates.ChildTextAsync(commands, ArchiveChildren.Preview, [0], cancellationToken).ConfigureAwait(false);
        return preview.Failure.Length > 0 ? ActionPreview.Unavailable("archive", preview.Failure) : Judged(context, ArchiveChildAnswers.Preview(preview.Text, context.Config));
    }

    /// <summary>D8: a timer run with less slack than <c>archive.minRunMinutes</c> skips; <c>null</c> otherwise.</summary>
    private static ActionPreview? NoTimeLeft(ActionContext context) =>
        context.Trigger == RunTrigger.Timer && Slack(context) < MinRun(context.Config)
            ? ArchiveGates.Skipped($"no time left in this run: {Minutes(Slack(context))} min of {ConfigKeys.Timer.RunLimitMinutes.Name} remain after the actions behind A13, below {ConfigKeys.Archive.MinRunMinutes.Name} ({context.Config.Int(ConfigKeys.Archive.MinRunMinutes)})")
            : null;

    public TriggerDecision Trigger(ActionPreview preview, EffectiveConfig config)
    {
        var due = preview.Facts.GetValueOrDefault(Fact);
        var removals = preview.Facts.GetValueOrDefault(RemovalsFact);
        return new TriggerDecision(due > 0 || removals > 0, string.Create(CultureInfo.InvariantCulture, $"{due} session(s) due to be archived, {removals} archived session(s) due to be removed"));
    }

    public async Task<ActionRun> RunAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken)
    {
        if (await BlockedAsync(context, commands, cancellationToken).ConfigureAwait(false) is { Length: > 0 } blocked)
        {
            return ArchiveGates.Failed(commands, blocked);
        }

        var budget = Budget(context);
        if (budget < MinRun(context.Config))
        {
            return ActionRun.Nothing(commands.Ran, $"no time left in this run ({Minutes(budget)} min)");
        }

        var ceiling = budget + TimeSpan.FromMinutes(context.Config.Int(ConfigKeys.Archive.FinishGraceMinutes));
        var answer = await ArchiveGates.StreamedAsync(context, commands, ArchiveChildren.Run, [Seconds(budget), context.RunId], ceiling, cancellationToken).ConfigureAwait(false);
        return answer.Failure.Length > 0 ? ArchiveGates.Failed(commands, answer.Failure) : Measured(commands, ArchiveChildAnswers.Run(answer.Text, context.Config));
    }

    /// <summary>Why the long child may not start: a recorded child still alive (9/9.4 #1), or a base the short child could not reach (D1).</summary>
    private static async Task<string> BlockedAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken) =>
        ArchiveChildren.Survivor(context, cancellationToken) is { Length: > 0 } survivor ? survivor
        : await UnreachedAsync(context, commands, cancellationToken).ConfigureAwait(false) is { Length: > 0 } unreached ? $"{unreached}; the long run was not started"
        : string.Empty;

    /// <summary>The short child (D1): empty when the base answered; otherwise why it did not.</summary>
    private static async Task<string> UnreachedAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken)
    {
        var text = await ArchiveGates.ChildTextAsync(commands, ArchiveChildren.Reach, [0, 1], cancellationToken).ConfigureAwait(false);
        return text.Failure.Length > 0 ? $"the base could not be checked: {text.Failure}"
            : ArchiveChildAnswers.Run(text.Text, context.Config) is ChildAnswer<ArchiveRunReport>.Valid { Value.Outcome: RunOutcomes.Done } ? string.Empty
            : $"the base did not answer within {ConfigKeys.Archive.ReachabilitySeconds.Name}, or the side's archive lock is held";
    }

    private static ActionPreview Judged(ActionContext context, ChildAnswer<ArchivePreviewReport> preview) => preview switch
    {
        ChildAnswer<ArchivePreviewReport>.Invalid bad => ActionPreview.Unavailable("archive", $"the archive child's preview could not be believed: {bad.Why}"),
        ChildAnswer<ArchivePreviewReport>.Valid p => Counted(context, p.Value),
        _ => throw new System.Diagnostics.UnreachableException("ChildAnswer is a closed set"),
    };

    /// <summary>The aggregates root keeps: per agent the sessions due and their bytes; the removals waiting; never a key.</summary>
    private static ActionPreview Counted(ActionContext context, ArchivePreviewReport preview)
    {
        var enabled = preview.Agents.Where(a => a.Enabled).ToList();
        var removals = preview.RemovalsDue;
        var due = enabled.Sum(a => a.DueUnits);
        var facts = new Dictionary<string, long>(StringComparer.Ordinal) { [Fact] = due, ["dueFiles"] = enabled.Sum(a => (long)a.DueFiles), [RemovalsFact] = removals, ["quarantined"] = enabled.Sum(a => (long)a.Quarantined) };
        IReadOnlyList<ActionItem> items = [.. enabled.Where(a => a.DueUnits > 0).Select(a => new ActionItem("agent", a.Id, a.DueBytes, string.Create(CultureInfo.InvariantCulture, $"{a.DueUnits} session(s) due, {a.DueFiles} file(s)")))];
        var what = string.Create(CultureInfo.InvariantCulture, $"move aged AI-agent sessions into the archive as {ArchiveGates.UserOf(context)}: {due} session(s) due, {removals} archived session(s) due to be removed");
        return ActionPreview.Of(what, due, enabled.Sum(a => a.DueBytes), "the archive child's own preview (archive preview --json), run as the target user; counts only", facts, string.Empty, items) with
        {
            Urgent = Urgent(enabled, context),
        };
    }

    /// <summary>D8: an agent whose oldest due session is within <c>archive.urgentWithinDays</c> of the agent's own deletion.</summary>
    private static string Urgent(IReadOnlyList<AgentPreviewReport> agents, ActionContext context)
    {
        var within = context.Config.Int(ConfigKeys.Archive.UrgentWithinDays);
        var now = context.Clock.GetUtcNow();
        var close = agents.Where(a => a.Retention.Known && a.OldestDueWriteUtc is { } oldest && a.Retention.Days - (now - oldest).TotalDays <= within).Select(a => a.Id).ToList();
        return close.Count == 0 ? string.Empty : $"{string.Join(", ", close)}: the oldest due session is within {ConfigKeys.Archive.UrgentWithinDays.Name} ({within}) days of the agent's own deletion";
    }

    /// <summary>The run child's judged answer as A13's measured result: counts per agent, the bytes the removals took — no key, no
    /// note, no first-skipped name of the child's.</summary>
    private static ActionRun Measured(ActionCommands commands, ChildAnswer<ArchiveRunReport> answer)
    {
        if (answer is not ChildAnswer<ArchiveRunReport>.Valid { Value: var report })
        {
            return ArchiveGates.Failed(commands, $"the archive child's answer could not be believed: {((ChildAnswer<ArchiveRunReport>.Invalid)answer).Why}");
        }

        IReadOnlyList<ActionItem> agents = [.. report.Agents.Select(a => new ActionItem("agent", a.Id, a.RemovedBytes, AgentLine(a)))];
        return new ActionRun(report.Agents.Sum(a => a.Copied + a.Removed), report.Agents.Sum(a => a.RemovedBytes), "the bytes the archive removed from the agents' folders once their copies were verified again — moved to the archive, not deleted", null, null, agents, commands.Ran, Fault(report))
        {
            Notes = [string.Create(CultureInfo.InvariantCulture, $"the archive child answered {report.Outcome}{Kind(report)} at {report.FilesPerSecond:0.0} files/s, {report.MegabytesPerSecond:0.00} MB/s")],
        };
    }

    private static string AgentLine(AgentRunReport a) =>
        string.Create(CultureInfo.InvariantCulture, $"copied {a.Copied} session(s) ({a.CopiedFiles} files, {a.CopiedBytes} bytes), removed {a.Removed}, gone at source {a.GoneAtSource}, superseded {a.Superseded}, damaged {a.Damaged}, waiting {a.Waiting}{string.Concat(a.Skipped.Select(s => $", skipped {s.Count} ({s.Rule})"))}");

    /// <summary>Empty for a run that did its work, stopped at a limit or found the side busy; the reason for a refusal, an
    /// unreachable base, no base or a fault stop.</summary>
    private static string Fault(ArchiveRunReport report) => report.Outcome switch
    {
        RunOutcomes.Done or RunOutcomes.Busy => string.Empty,
        RunOutcomes.Stopped when !StopKinds.IsFault(report.StopKind) => string.Empty,
        _ => $"the archive child answered {report.Outcome}{Kind(report)}; \"wsl-care archive status\" and \"wsl-care archive list --run {report.RunId}\", run as the user, say more",
    };

    private static string Kind(ArchiveRunReport report) => report.StopKind.Length > 0 ? $" ({report.StopKind})" : string.Empty;

    /// <summary>D8: a button's run takes <c>archive.runBudgetMinutes</c>; a timer's at most what is left of the run limit after the
    /// actions behind A13 and the margin.</summary>
    private static TimeSpan Budget(ActionContext context)
    {
        var budget = TimeSpan.FromMinutes(context.Config.Int(ConfigKeys.Archive.RunBudgetMinutes));
        return context.Trigger == RunTrigger.Timer && Slack(context) < budget ? Slack(context) : budget;
    }

    /// <summary>What a timer run has left after the actions behind A13 (A20, A1, A2) at their worst and the run margin; a context
    /// without its run's start has none.</summary>
    private static TimeSpan Slack(ActionContext context)
    {
        var behind = ActionRegistry.Product.InExecutionOrder([.. ActionId.ExecutionOrder.SkipWhile(id => id.Text != "A13").Skip(1)]);
        var end = context.RunStarted == DateTimeOffset.MinValue ? context.Clock.GetUtcNow() : context.RunStarted + TimeSpan.FromMinutes(context.Config.Int(ConfigKeys.Timer.RunLimitMinutes));
        return end - context.Clock.GetUtcNow() - RunBudget.WorstCaseOf(behind, context.Config) - RunBudget.Margin;
    }

    private static TimeSpan MinRun(EffectiveConfig config) => TimeSpan.FromMinutes(config.Int(ConfigKeys.Archive.MinRunMinutes));

    private static string Seconds(TimeSpan budget) => ((long)Math.Floor(budget.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

    private static string Minutes(TimeSpan span) => Math.Max(0, Math.Floor(span.TotalMinutes)).ToString(CultureInfo.InvariantCulture);
}
