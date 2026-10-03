using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Docker;
using WslCare.Core.Hosting;
using WslCare.Core.Preview;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;

namespace WslCare.Core.Actions.DockerCleanups;

/// <summary>
/// A4 (plan §5, §15 #4, §15c #1): <c>docker volume rm</c> of a RE-CHECKED list — never <c>prune</c>. At run time it removes
/// only the anonymous volumes that are in this run's LIVE preview (the dangling list Docker answers now ∩ a 64-hex name ∖
/// the <c>wsl-care.keep=true</c> label ∩ first seen unattached at least <c>volumes.anonymousOlderThanDays</c> ago) AND —
/// for a button — in the list the panel SHOWED (<c>--volume</c> / <c>--only</c>). It refuses on Docker below 23, and on a
/// Docker whose version it cannot read.
/// </summary>
/// <remarks>
/// <para><b>Freed bytes</b> (§15c #1): the <c>docker system df -v</c> sizes, read just before the removal (the preview's
/// look), of EXACTLY the volumes <c>docker volume rm</c> printed back; a volume it did not confirm counts nothing.
/// Docker's own "Local Volumes" total before and after is recorded beside it as a cross-check, never as the figure.</para>
/// <para><b>Idempotent per target</b> (§15a #0): a volume already gone is <i>already gone</i>, one a container attached
/// since the preview is refused by Docker itself (no <c>-f</c>) and kept — neither is a failure.</para>
/// <para>After the removal it re-reads the unattached volumes and records the first sightings (§15b #3: only collect and
/// the actions record them) — the removed names leave the record, the others keep their first sighting.</para>
/// </remarks>
public sealed class VolumeRemoval : ICleanupAction
{
    /// <summary>Docker's own "Local Volumes" total of the preview's look — the cross-check's "before".</summary>
    public const string VolumesTotalFact = "volumesTotalBytes";

    /// <summary>How many names of the shown list are no longer candidates (gone, attached again, labelled, too young).</summary>
    public const string ShownNotCandidatesFact = "shownNotCandidates";

    public const string ButtonNeedsShownList =
        "a button run of A4 must pass the volumes its preview SHOWED (--volume or --only): A4 removes only those, re-checked (plan 15 #4)";

    private const long Gib = 1L << 30;

    public ActionId Id { get; } = ActionId.Find("A4")!;

    public string Summary => "docker volume rm of anonymous volumes no container uses, first seen unattached long enough ago (re-checked, never prune)";

    public CommandScope Scope => CommandScope.Machine;

    public IdleRule Idle => IdleRule.Never;

    public IReadOnlyList<HostSide> Sides { get; } = [HostSide.Wsl];

    public IReadOnlyList<CommandTemplate> Commands { get; } = [.. DockerCleanupCommands.Reads, DockerCleanupCommands.VolumeRemove];

    public async Task<ActionPreview> PreviewAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken)
    {
        var look = await DockerLook.TakeAsync(context, commands, cancellationToken).ConfigureAwait(false);
        var row = CleanupPreviews.A4Row(look.Snapshot, look.Seen, context.Config, look.Now);
        var targets = Reading.Combine(look.Snapshot.Inventory, look.Snapshot.Dangling, (inventory, dangling) =>
            CleanupTargets.AnonymousVolumes(inventory, dangling, look.Seen, CleanupPreviews.A4Cutoff(context.Config, look.Now)).Selected.Select(t => RowPreviews.Item("volume", t)).ToList()).ValueOr([]);
        var facts = DockerCleanupAnswers.TypeSize(look.Snapshot.Totals, DockerTotal.Volumes) is Reading<long>.Available { Value: var total }
            ? new Dictionary<string, long>(StringComparer.Ordinal) { [VolumesTotalFact] = total }
            : new Dictionary<string, long>(StringComparer.Ordinal);
        var preview = RowPreviews.From(row, targets, facts);
        return preview.Available ? Shown(preview, context) : preview;
    }

    /// <summary>Plan §5 A4: more than <c>volumes.anonymousMaxCount</c> volumes, or more than <c>volumes.anonymousMaxGb</c>.</summary>
    public TriggerDecision Trigger(ActionPreview preview, EffectiveConfig config)
    {
        var maxCount = config.Int(ConfigKeys.Volumes.AnonymousMaxCount);
        var maxGb = config.Int(ConfigKeys.Volumes.AnonymousMaxGb);
        var bytes = preview.Bytes ?? 0;
        return new TriggerDecision(
            preview.Count > maxCount || bytes > maxGb * Gib,
            string.Create(CultureInfo.InvariantCulture, $"{preview.Count} volumes, {bytes / (double)Gib:0.00} GiB; the trigger is above {maxCount} volumes or above {maxGb} GiB"));
    }

    public async Task<ActionRun> RunAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken)
    {
        if (preview.Targets.Count == 0)
        {
            return ActionRun.Nothing(commands.Ran, "no volume to remove");
        }

        var removal = await DockerRemovals.RemoveAsync(commands, DockerCleanupCommands.VolumeRemove, preview.Targets, new Dictionary<string, string>(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
        var after = await DockerRemovals.TotalAsync(commands, DockerTotal.Volumes, cancellationToken).ConfigureAwait(false);
        var sightings = await RecordSightingsAsync(context, commands, cancellationToken).ConfigureAwait(false);
        return new ActionRun(
            removal.Removed.Count,
            removal.Removed.Sum(v => v.Bytes ?? 0),
            "the docker system df -v sizes, read just before the removal, of exactly the volumes docker volume rm confirmed (Docker's Local Volumes total before/after beside it)",
            preview.Facts.TryGetValue(VolumesTotalFact, out var before) ? before : null,
            after,
            removal.Removed,
            commands.Ran,
            removal.Failure)
        {
            NotRemoved = removal.NotRemoved,
            Notes = [sightings],
        };
    }

    /// <summary>The preview narrowed to what the panel showed — or, for a button that showed nothing, refused.</summary>
    private static ActionPreview Shown(ActionPreview preview, ActionContext context)
    {
        if (context.ShownVolumes.Given)
        {
            var names = context.ShownVolumes.Names;
            var kept = preview.Targets.Where(t => names.Contains(t.Key)).ToList();
            var facts = new Dictionary<string, long>(preview.Facts, StringComparer.Ordinal) { [ShownNotCandidatesFact] = names.Count - kept.Count };
            var what = string.Create(CultureInfo.InvariantCulture, $"{preview.What}; of the {names.Count} volume(s) the panel showed, the {kept.Count} still candidates");
            return RowPreviews.Narrowed(preview, kept, what, facts);
        }

        return context.Trigger == RunTrigger.Manual
            ? preview with { Refusal = preview.Refusal.Length > 0 ? preview.Refusal : ButtonNeedsShownList }
            : preview;
    }

    /// <summary>Plan §15b #3: the action records first sightings — the unattached volumes as Docker lists them NOW.</summary>
    private static async Task<string> RecordSightingsAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken)
    {
        var listed = DockerCli.Classify(DockerCommands.DanglingVolumes, await commands.RunAsync(DockerCleanupCommands.DanglingRead, [], cancellationToken).ConfigureAwait(false));
        if (listed is not DockerAnswer.Answered answered)
        {
            return $"first sightings not recorded: {((DockerAnswer.Failed)listed).Problem.Reason}";
        }

        var store = new VolumeSeenStore(context.Paths, context.Files);
        var anonymous = DanglingVolumes.Parse(answered.Stdout).Where(DockerJson.IsFullId);
        return store.TryWrite(store.Read().Record.Observe(anonymous, context.Clock.GetUtcNow())) switch
        {
            VolumeSeenWrite.Written => "first sightings recorded (volume-seen.json)",
            VolumeSeenWrite.NotWritten n => $"first sightings not recorded: {n.Reason}",
            _ => throw new System.Diagnostics.UnreachableException("VolumeSeenWrite is a closed set"),
        };
    }
}

/// <summary>What a batched removal did: the targets removed, those not removed and why, and the failure (empty when none).</summary>
public sealed record RemovalResult(IReadOnlyList<ActionItem> Removed, IReadOnlyList<ActionItem> NotRemoved, string Failure);

/// <summary>The batched removal A4 and A5 share: <see cref="DockerCleanupCommands.Batch"/> names per command, each name judged
/// by what Docker printed (<see cref="DockerCleanupAnswers.Removal(IReadOnlyList{string}, string, string, IReadOnlyDictionary{string, string})"/>);
/// a command that did not run stops the rest (nothing is retried blind).</summary>
public static class DockerRemovals
{
    public static async Task<RemovalResult> RemoveAsync(ActionCommands commands, CommandTemplate template, IReadOnlyList<ActionItem> targets, IReadOnlyDictionary<string, string> aliases, CancellationToken cancellationToken)
    {
        var removed = new List<ActionItem>();
        var notRemoved = new List<ActionItem>();
        var failures = new List<string>();
        var judged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var batch in targets.Chunk(DockerCleanupCommands.Batch))
        {
            var outcome = await commands.RunAsync(template, [.. batch.Select(t => t.Key)], cancellationToken).ConfigureAwait(false);
            if (CommandFailures.NotRun(template.Shape, outcome) is { Length: > 0 } notRun)
            {
                failures.Add(notRun);
                notRemoved.AddRange(targets.Where(t => !judged.Contains(t.Key)).Select(t => t with { Note = "not attempted: " + notRun }));
                break;
            }

            var exited = (CommandOutcome.Exited)outcome;
            var verdicts = DockerCleanupAnswers.Removal([.. batch.Select(t => t.Key)], exited.Stdout.Text, exited.Stderr.Text, aliases);
            foreach (var target in batch.Where(t => judged.Add(t.Key)))
            {
                var (verdict, detail) = verdicts[target.Key];
                Judge(target, verdict, detail, removed, notRemoved, failures);
            }
        }

        return new RemovalResult(removed, notRemoved, string.Join("; ", failures.Take(5)));
    }

    /// <summary>Docker's own total of one type now (<c>docker system df</c>), or <c>null</c> when it did not answer.</summary>
    public static async Task<long?> TotalAsync(ActionCommands commands, string type, CancellationToken cancellationToken)
    {
        var answer = DockerCli.Classify(DockerCommands.SystemDf, await commands.RunAsync(DockerCleanupCommands.SystemDfRead, [], cancellationToken).ConfigureAwait(false));
        return answer is DockerAnswer.Answered a && DockerCleanupAnswers.TypeSize(DockerTotal.Parse(a.Stdout), type) is Reading<long>.Available { Value: var bytes } ? bytes : null;
    }

    private static void Judge(ActionItem target, RemovalVerdict verdict, string detail, List<ActionItem> removed, List<ActionItem> notRemoved, List<string> failures)
    {
        switch (verdict)
        {
            case RemovalVerdict.Removed:
                removed.Add(target);
                break;
            case RemovalVerdict.AlreadyGone:
                notRemoved.Add(target with { Note = "already gone: " + detail });
                break;
            case RemovalVerdict.InUse:
                notRemoved.Add(target with { Note = "kept by Docker, in use since the preview: " + detail });
                break;
            default:
                notRemoved.Add(target with { Note = "not confirmed: " + detail });
                failures.Add($"{target.Name}: {detail}");
                break;
        }
    }
}
