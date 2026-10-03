using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Docker;
using WslCare.Core.Hosting;
using WslCare.Core.Preview;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;

namespace WslCare.Core.Actions.DockerCleanups;

/// <summary>
/// A7 (plan §5): the build cache. On the TIMER, <c>docker builder prune -f</c> with a SIZE CAP — <c>--max-used-space
/// &lt;buildCache.maxGb&gt;GB</c>, or <c>--keep-storage</c> on a builder that predates it — whichever THIS Docker's
/// <c>builder prune --help</c> lists (detected, never guessed; neither → the timer refuses with the reason). A button (or a
/// terminal) prunes ALL of it: <c>docker builder prune -a -f</c>. An age filter alone is not used: on 2026-10-02 it freed
/// nothing (all 34.6 GB were younger than 7 days).
/// </summary>
/// <remarks>Heavy: waits for an idle machine even on a button (E3.S1's <see cref="IdleRule.Always"/>). Freed bytes are
/// Docker's own "Total:" (plan §5); the removed objects the entries its table listed. Measured 2026-10-02 on Docker
/// 29.6.1 (buildx is the builder): the help lists <c>--max-used-space</c>, <c>--reserved-space</c>, <c>--min-free-space</c>
/// and no <c>--keep-storage</c> (the capture is <c>tests/fixtures/docker/ubuntu-2026-10-02/builder-prune-help.out</c>).</remarks>
public sealed class BuildCachePrune : ICleanupAction
{
    /// <summary>The fact the trigger reads: the whole cache's size, every entry (Docker's <c>system df -v</c>).</summary>
    public const string CacheBytesFact = "cacheBytes";

    /// <summary>The fact that says which cap the timer would pass: 1 = <c>--max-used-space</c>, 2 = <c>--keep-storage</c>,
    /// absent = neither.</summary>
    public const string CapFlagFact = "capFlag";

    private const long Gib = 1L << 30;

    public ActionId Id { get; } = ActionId.Find("A7")!;

    public string Summary => "docker builder prune: the timer down to buildCache.maxGb (size cap), a button all of it (-a)";

    public CommandScope Scope => CommandScope.Machine;

    public IdleRule Idle => IdleRule.Always;

    public IReadOnlyList<HostSide> Sides { get; } = [HostSide.Wsl];

    public IReadOnlyList<CommandTemplate> Commands { get; } =
    [
        .. DockerCleanupCommands.Reads, DockerCleanupCommands.BuilderPruneHelp, DockerCleanupCommands.BuilderPruneMaxUsed,
        DockerCleanupCommands.BuilderPruneKeepStorage, DockerCleanupCommands.BuilderPruneAll,
    ];

    public async Task<ActionPreview> PreviewAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken)
    {
        var look = await DockerLook.TakeAsync(context, commands, cancellationToken).ConfigureAwait(false);
        var capGb = context.Config.Int(ConfigKeys.BuildCache.MaxGb);
        var row = CleanupPreviews.A7Row(look.Snapshot, context.Config, look.Now);
        var selection = look.Snapshot.Inventory.Map(inventory => CleanupTargets.BuildCache(inventory, look.Now.AddDays(-context.Config.Int(ConfigKeys.BuildCache.OlderThanDays)), capGb));
        var facts = new Dictionary<string, long>(StringComparer.Ordinal);
        if (selection is Reading<CacheTargets>.Available { Value: var cache })
        {
            facts[CacheBytesFact] = cache.TotalBytes;
        }

        var preview = RowPreviews.From(row, selection.Map(s => s.Selected.Select(t => RowPreviews.Item("build cache", t)).ToList()).ValueOr([]), facts);
        return preview.Available && context.Trigger == RunTrigger.Timer ? await CappedAsync(preview, capGb, commands, cancellationToken).ConfigureAwait(false) : preview;
    }

    /// <summary>Plan §5 A7: the cache above <c>buildCache.maxGb</c>.</summary>
    public TriggerDecision Trigger(ActionPreview preview, EffectiveConfig config)
    {
        var capGb = config.Int(ConfigKeys.BuildCache.MaxGb);
        return preview.Facts.TryGetValue(CacheBytesFact, out var bytes)
            ? new TriggerDecision(bytes > capGb * Gib, string.Create(CultureInfo.InvariantCulture, $"the build cache holds {bytes / (double)Gib:0.00} GiB; the trigger is above {capGb} GiB"))
            : new TriggerDecision(false, "the build cache's size was not read");
    }

    public async Task<ActionRun> RunAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken)
    {
        if (preview.Targets.Count == 0)
        {
            return ActionRun.Nothing(commands.Ran, "no reclaimable build cache");
        }

        var (template, values) = context.Trigger == RunTrigger.Timer
            ? (preview.Facts.GetValueOrDefault(CapFlagFact) == 2 ? DockerCleanupCommands.BuilderPruneKeepStorage : DockerCleanupCommands.BuilderPruneMaxUsed, (IReadOnlyList<string>)[DockerCleanupCommands.Cap(context.Config.Int(ConfigKeys.BuildCache.MaxGb))])
            : (DockerCleanupCommands.BuilderPruneAll, []);
        var outcome = await commands.RunAsync(template, values, cancellationToken).ConfigureAwait(false);
        if (outcome is not Processes.CommandOutcome.Exited exited)
        {
            return new ActionRun(0, null, "nothing was pruned", null, null, [], commands.Ran, CommandFailures.NotRun(template.Shape, outcome));
        }

        var removed = DockerCleanupAnswers.PrunedCache(exited.Stdout.Text);
        var reclaimed = DockerCleanupAnswers.Reclaimed(exited.Stdout.Text);
        return new ActionRun(
            removed.Count,
            reclaimed is Reading<long>.Available { Value: var bytes } ? bytes : null,
            reclaimed.IsAvailable ? "Docker's own Total" : $"unknown: {reclaimed.ReasonOrEmpty}",
            preview.Facts.TryGetValue(CacheBytesFact, out var before) ? before : null,
            null,
            removed,
            commands.Ran,
            CommandFailures.Of(template.Shape, outcome));
    }

    /// <summary>The timer's preview: which cap THIS Docker takes, or a refusal naming the reason.</summary>
    private static async Task<ActionPreview> CappedAsync(ActionPreview preview, int capGb, ActionCommands commands, CancellationToken cancellationToken)
    {
        var help = DockerCli.Classify(DockerCleanupCommands.BuilderPruneHelpCommand, await commands.RunAsync(DockerCleanupCommands.BuilderPruneHelp, [], cancellationToken).ConfigureAwait(false));
        var flag = help is DockerAnswer.Answered a ? DockerCleanupAnswers.CapFlag(a.Stdout) : string.Empty;
        var facts = new Dictionary<string, long>(preview.Facts, StringComparer.Ordinal);
        if (flag.Length > 0)
        {
            facts[CapFlagFact] = flag == "--max-used-space" ? 1 : 2;
        }

        var why = help is DockerAnswer.Failed f ? f.Problem.Reason : "its help lists neither --max-used-space nor --keep-storage";
        return preview with
        {
            What = string.Create(CultureInfo.InvariantCulture, $"{preview.What}; the timer prunes down to {capGb} GB{(flag.Length > 0 ? $" ({flag})" : string.Empty)}"),
            Facts = facts,
            Refusal = flag.Length > 0 ? preview.Refusal : $"this Docker's builder prune takes no size cap ({why}): the timer's capped prune refuses; a button may prune all of it",
        };
    }
}
