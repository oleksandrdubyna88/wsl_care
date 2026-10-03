using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Hosting;
using WslCare.Core.Preview;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;

namespace WslCare.Core.Actions.DockerCleanups;

/// <summary>
/// A6 and A6Unused (plan §5): <c>docker image prune -f</c> (dangling images) and <c>docker image prune -a -f --filter
/// until=&lt;images.unusedOlderThanDays×24&gt;h</c> (every image no container uses, created that long ago) — both with
/// <c>--filter label!=wsl-care.keep=true</c>. An image any container uses, even a stopped one, is never removed: Docker's
/// prune rule, and the preview's own selection excludes every image an inspected container was created from.
/// </summary>
/// <remarks>
/// <para><b>Freed bytes</b> are Docker's own "Total reclaimed space" (plan §5), the removed objects the <c>deleted:</c> /
/// <c>untagged:</c> lines it printed. When the live preview selects nothing, no prune is started at all — a prune
/// is Docker's decision, and it is asked only when the preview found something to take.</para>
/// <para>A6Unused's <c>-a</c> also takes a DANGLING image of that age (A6's target, which the run order puts first).</para>
/// </remarks>
public sealed class ImagePrune(bool unused) : ICleanupAction
{
    private const long Gib = 1L << 30;

    public ActionId Id { get; } = ActionId.Find(unused ? "A6Unused" : "A6")!;

    public string Summary => unused
        ? "docker image prune -a --filter until=: images no container uses, created at least images.unusedOlderThanDays ago"
        : "docker image prune: dangling images no container uses";

    public CommandScope Scope => CommandScope.Machine;

    public IdleRule Idle => IdleRule.Never;

    public IReadOnlyList<HostSide> Sides { get; } = [HostSide.Wsl];

    public IReadOnlyList<CommandTemplate> Commands { get; } = [.. DockerCleanupCommands.Reads, unused ? DockerCleanupCommands.ImagePruneUnused : DockerCleanupCommands.ImagePruneDangling];

    public async Task<ActionPreview> PreviewAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken)
    {
        var look = await DockerLook.TakeAsync(context, commands, cancellationToken).ConfigureAwait(false);
        var row = CleanupPreviews.A6Row(look.Snapshot, context.Config, look.Now, unused);
        var targets = Reading.Combine(look.Snapshot.Inventory, look.Snapshot.Details, (inventory, details) =>
            CleanupTargets.UnusedImages(inventory, details, dangling: !unused, CleanupPreviews.A6Cutoff(context.Config, look.Now, unused)).Selected.Select(t => RowPreviews.Item("image", t)).ToList()).ValueOr([]);
        return RowPreviews.From(row, targets, new Dictionary<string, long>(StringComparer.Ordinal));
    }

    /// <summary>Plan §5 A6: dangling — any; unused — more than <c>images.unusedMaxGb</c> reclaimable.</summary>
    public TriggerDecision Trigger(ActionPreview preview, EffectiveConfig config)
    {
        if (!unused)
        {
            return new TriggerDecision(preview.Count > 0, string.Create(CultureInfo.InvariantCulture, $"{preview.Count} dangling image(s); the trigger is any"));
        }

        var maxGb = config.Int(ConfigKeys.Images.UnusedMaxGb);
        var bytes = preview.Bytes ?? 0;
        return new TriggerDecision(bytes > maxGb * Gib, string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)Gib:0.00} GiB of unused images; the trigger is above {maxGb} GiB"));
    }

    public async Task<ActionRun> RunAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken)
    {
        if (preview.Targets.Count == 0)
        {
            return ActionRun.Nothing(commands.Ran, "no image to remove");
        }

        var template = unused ? DockerCleanupCommands.ImagePruneUnused : DockerCleanupCommands.ImagePruneDangling;
        var hours = (long)context.Config.Int(ConfigKeys.Images.UnusedOlderThanDays) * 24;
        IReadOnlyList<string> values = unused ? [string.Create(CultureInfo.InvariantCulture, $"until={hours}h")] : [];
        var outcome = await commands.RunAsync(template, values, cancellationToken).ConfigureAwait(false);
        return Measured(template.Shape, outcome, commands);
    }

    /// <summary>What a prune printed, as the run's result: Docker's total, the objects it named, its failure.</summary>
    internal static ActionRun Measured(string shown, CommandOutcome outcome, ActionCommands commands)
    {
        if (outcome is not CommandOutcome.Exited exited)
        {
            return new ActionRun(0, null, "nothing was pruned", null, null, [], commands.Ran, CommandFailures.NotRun(shown, outcome));
        }

        var removed = DockerCleanupAnswers.PrunedImages(exited.Stdout.Text);
        var reclaimed = DockerCleanupAnswers.Reclaimed(exited.Stdout.Text);
        return new ActionRun(
            removed.Count(i => i.Kind == "image"),
            reclaimed is Reading<long>.Available { Value: var bytes } ? bytes : null,
            reclaimed.IsAvailable ? "Docker's own Total reclaimed space" : $"unknown: {reclaimed.ReasonOrEmpty}",
            null,
            null,
            removed,
            commands.Ran,
            CommandFailures.Of(shown, outcome));
    }
}
