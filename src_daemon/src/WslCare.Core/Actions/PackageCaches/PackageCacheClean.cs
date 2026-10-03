using System.Globalization;

using WslCare.Core.Actions.UserCaches;
using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Health;
using WslCare.Core.Hosting;
using WslCare.Core.Preview;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;

namespace WslCare.Core.Actions.PackageCaches;

/// <summary>
/// A9 (plan §5): <c>apt-get clean</c>, then <c>snap remove &lt;name&gt; --revision=&lt;n&gt;</c> of every DISABLED snap
/// revision — package caches and superseded revisions only. Machine-scoped (root, the daemon). A tool that is not
/// installed skips its part with the reason; both missing is a skip of the action, never an error.
/// </summary>
/// <remarks>
/// <para><b>Preview</b> = the A9 row of <c>preview --all</c> (the apt cache and the disabled revisions the newest full run
/// measured, with its age); its items are the disabled revisions <c>snap list --all</c> answers NOW.</para>
/// <para><b>Run</b>: the disabled revisions are re-listed at run time (plan §15a #0) and only a revision that is STILL
/// disabled, with a numeric revision and a valid snap name, is removed; snapd itself refuses the active revision. Freed
/// bytes are measured: <c>/var/cache/apt</c> walked before and after, plus the size (read before) of each
/// <c>{name}_{revision}.snap</c> that is gone after.</para>
/// </remarks>
public sealed class PackageCacheClean : ICleanupAction
{
    /// <summary>Plan §5 A9's trigger: the apt cache above 200 MB (binary, as every size the actions compare).</summary>
    public const long AptTriggerBytes = 200L * 1024 * 1024;

    public const string AptBytesFact = "aptBytes";
    public const string DisabledRevisionsFact = "disabledRevisions";

    public static readonly CommandTemplate AptClean = new("apt-get-clean", CommandScope.Machine, "apt-get", [new ArgPart.Literal("clean")], TimeSpan.FromMinutes(5), CommandRequest.DefaultOutputCapChars);

    public static readonly CommandTemplate SnapList = CommandTemplate.Fixed(HealthCommands.SnapList);

    public static readonly CommandTemplate SnapRemove = new(
        "snap-remove-revision",
        CommandScope.Machine,
        HealthCommands.Snap,
        [new ArgPart.Literal("remove"), new ArgPart.Slot("snap", new SlotKind.SnapName()), new ArgPart.Slot("revision", new SlotKind.Prefixed("--revision=", new SlotKind.Number(1, 999_999_999)))],
        TimeSpan.FromMinutes(5),
        CommandRequest.DefaultOutputCapChars);

    public ActionId Id { get; } = ActionId.Find("A9")!;

    public string Summary => "apt-get clean, and snap remove --revision of disabled snap revisions only";

    public CommandScope Scope => CommandScope.Machine;

    public IdleRule Idle => IdleRule.Never;

    public IReadOnlyList<HostSide> Sides { get; } = [HostSide.Wsl];

    public IReadOnlyList<CommandTemplate> Commands { get; } = [AptClean, SnapList, SnapRemove];

    public async Task<ActionPreview> PreviewAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken)
    {
        var row = CleanupPreviews.A9Row(context.Config, LastFullRun.Read(context.Paths, context.Files, context.Clock).Folders);
        var snaps = await DisabledAsync(commands, cancellationToken).ConfigureAwait(false);
        var preview = RowPreviews.From(row, [.. snaps.Revisions.Select(r => Item(context, r))], Facts(row));
        return snaps.NotInstalled && commands.Locate(AptClean) is ResolvedExecutable.NotFound
            ? preview with { Skip = "neither apt-get nor snap is installed" }
            : preview;
    }

    /// <summary>Plan §5 A9: the apt cache above 200 MB, or any disabled revision.</summary>
    public TriggerDecision Trigger(ActionPreview preview, EffectiveConfig config)
    {
        var apt = preview.Facts.GetValueOrDefault(AptBytesFact);
        var revisions = preview.Facts.GetValueOrDefault(DisabledRevisionsFact);
        return new TriggerDecision(apt > AptTriggerBytes || revisions > 0, string.Create(CultureInfo.InvariantCulture, $"apt cache {apt / 1e6:0.0} MB, {revisions} disabled snap revision(s); the trigger is above 200 MiB or any revision"));
    }

    public async Task<ActionRun> RunAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken)
    {
        var linux = (LinuxHostPaths)context.Paths;
        var notes = new List<string>();
        var failures = new List<string>();
        var removed = new List<ActionItem>();
        var aptBefore = CacheFolders.Measure(context.Files, linux.AptCacheDirectory, cancellationToken);
        var aptFreed = await AptAsync(context, commands, linux, aptBefore, notes, failures, cancellationToken).ConfigureAwait(false);
        if (aptFreed is > 0)
        {
            removed.Add(new ActionItem("apt cache", linux.AptCacheDirectory, aptFreed));
        }

        var snapFreed = await SnapsAsync(context, commands, linux, removed, notes, failures, cancellationToken).ConfigureAwait(false);
        return new ActionRun(
            removed.Count,
            aptFreed is null && snapFreed == 0 ? null : (aptFreed ?? 0) + snapFreed,
            "/var/cache/apt walked before and after apt-get clean, plus the sizes (read before) of the snap files gone after snap remove",
            aptBefore.CompleteBytes,
            null,
            removed,
            commands.Ran,
            string.Join("; ", failures))
        {
            Notes = notes,
        };
    }

    private static async Task<long?> AptAsync(ActionContext context, ActionCommands commands, LinuxHostPaths linux, FolderReading before, List<string> notes, List<string> failures, CancellationToken cancellationToken)
    {
        var outcome = await commands.RunAsync(AptClean, [], cancellationToken).ConfigureAwait(false);
        if (outcome is CommandOutcome.FailedToStart missing)
        {
            notes.Add($"apt-get clean skipped: apt-get is not installed ({missing.Reason})");
            return null;
        }

        if (CommandFailures.Of("apt-get clean", outcome) is { Length: > 0 } failed)
        {
            failures.Add(failed);
        }

        return CacheFolders.Freed(before, CacheFolders.Measure(context.Files, linux.AptCacheDirectory, cancellationToken));
    }

    /// <summary>Every revision STILL disabled now: removed one by one, each measured by its file.</summary>
    private static async Task<long> SnapsAsync(ActionContext context, ActionCommands commands, LinuxHostPaths linux, List<ActionItem> removed, List<string> notes, List<string> failures, CancellationToken cancellationToken)
    {
        var snaps = await DisabledAsync(commands, cancellationToken).ConfigureAwait(false);
        if (snaps.NotInstalled || snaps.Problem.Length > 0)
        {
            notes.Add(snaps.NotInstalled ? $"snap revisions skipped: snap is not installed ({snaps.Problem})" : $"snap revisions not removed: {snaps.Problem}");
            return 0;
        }

        var freed = 0L;
        foreach (var revision in snaps.Revisions)
        {
            freed += await RemoveAsync(context, commands, linux, revision, removed, notes, failures, cancellationToken).ConfigureAwait(false);
        }

        return freed;
    }

    private static async Task<long> RemoveAsync(ActionContext context, ActionCommands commands, LinuxHostPaths linux, SnapRevision revision, List<ActionItem> removed, List<string> notes, List<string> failures, CancellationToken cancellationToken)
    {
        if (!new SlotKind.SnapName().Accepts(revision.Name) || !new SlotKind.Number(1, 999_999_999).Accepts(revision.Revision))
        {
            notes.Add($"kept {Printable(revision.Name)} revision {Printable(revision.Revision)}: not a snap name and numeric revision this action removes");
            return 0;
        }

        var file = SnapFile(linux, revision);
        var size = context.Files.FileSize(file) is FileSizeResult.Measured m ? m.Bytes : (long?)null;
        var outcome = await commands.RunAsync(SnapRemove, [revision.Name, $"--revision={revision.Revision}"], cancellationToken).ConfigureAwait(false);
        if (CommandFailures.Of($"snap remove {revision.Name} --revision={revision.Revision}", outcome) is { Length: > 0 } failed)
        {
            failures.Add(failed);
            return 0;
        }

        var gone = context.Files.FileSize(file) is FileSizeResult.Missing;
        removed.Add(new ActionItem("snap revision", $"{revision.Name} {revision.Revision}", gone ? size : null, gone ? string.Empty : "its file is still there: not counted"));
        return gone ? size ?? 0 : 0;
    }

    /// <summary>The disabled revisions snap lists NOW — or that snap is not installed, or why it could not be asked.</summary>
    private sealed record Disabled(IReadOnlyList<SnapRevision> Revisions, bool NotInstalled, string Problem);

    private static async Task<Disabled> DisabledAsync(ActionCommands commands, CancellationToken cancellationToken)
    {
        var outcome = await commands.RunAsync(SnapList, [], cancellationToken).ConfigureAwait(false);
        return ToolAnswers.Read(HealthCommands.SnapList, outcome) switch
        {
            Reading<string>.Available { Value: var stdout } => new Disabled(HealthParsers.DisabledSnapRevisions(stdout), false, string.Empty),
            var failed => new Disabled([], outcome is CommandOutcome.FailedToStart, failed.ReasonOrEmpty),
        };
    }

    private static ActionItem Item(ActionContext context, SnapRevision revision) =>
        new("snap revision", $"{revision.Name} {revision.Revision}", context.Paths is LinuxHostPaths linux && context.Files.FileSize(SnapFile(linux, revision)) is FileSizeResult.Measured m ? m.Bytes : null, "disabled");

    private static string SnapFile(LinuxHostPaths linux, SnapRevision revision) => linux.Rules.Join(linux.SnapFilesDirectory, $"{revision.Name}_{revision.Revision}.snap");

    /// <summary>The row's two notes, as the facts the trigger reads.</summary>
    private static Dictionary<string, long> Facts(CleanupRow row)
    {
        var facts = new Dictionary<string, long>(StringComparer.Ordinal);
        if (row.Figures is Reading<RowFigures>.Available { Value: var f })
        {
            facts[AptBytesFact] = f.Notes.FirstOrDefault(n => n.What == CleanupPreviews.AptCacheNote)?.Bytes.ValueOr(0) ?? 0;
            facts[DisabledRevisionsFact] = f.Notes.FirstOrDefault(n => n.What == CleanupPreviews.SnapRevisionsNote)?.Count ?? 0;
        }

        return facts;
    }

    private static string Printable(string text) => new([.. text.Take(60).Select(c => char.IsControl(c) ? '?' : c)]);
}
