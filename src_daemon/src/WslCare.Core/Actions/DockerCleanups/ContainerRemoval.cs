using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Docker;
using WslCare.Core.Hosting;
using WslCare.Core.Preview;
using WslCare.Core.Processes.Policy;

namespace WslCare.Core.Actions.DockerCleanups;

/// <summary>
/// A5 and A5Testcontainers (plan §5): <c>docker rm -v</c> of the containers stopped at least the limit ago —
/// <c>containers.stoppedOlderThanDays</c> for the others, <c>containers.testcontainersOlderThanHours</c> for those carrying
/// the <c>org.testcontainers</c> label — with the ANONYMOUS volumes they hold (named volumes stay). A container carrying
/// <c>wsl-care.keep=true</c> is never selected; a container that started since the preview is refused by Docker itself
/// (no <c>-f</c>) and kept.
/// </summary>
/// <remarks><b>Freed bytes</b> (plan §5: "the docker system df -v sizes of the removed objects"): the writable layer of each
/// container <c>docker rm</c> confirmed, plus each anonymous volume of a confirmed container that Docker no longer LISTS
/// afterwards (<c>docker volume ls</c>) — a volume it cannot confirm gone counts nothing.</remarks>
public sealed class ContainerRemoval(bool testcontainers) : ICleanupAction
{
    public ActionId Id { get; } = ActionId.Find(testcontainers ? "A5Testcontainers" : "A5")!;

    public string Summary => testcontainers
        ? "docker rm -v of Testcontainers containers stopped at least containers.testcontainersOlderThanHours ago"
        : "docker rm -v of containers stopped at least containers.stoppedOlderThanDays ago (not Testcontainers)";

    public CommandScope Scope => CommandScope.Machine;

    public IdleRule Idle => IdleRule.Never;

    public IReadOnlyList<HostSide> Sides { get; } = [HostSide.Wsl];

    public IReadOnlyList<CommandTemplate> Commands { get; } = [.. DockerCleanupCommands.Reads, DockerCleanupCommands.ContainerRemove];

    public async Task<ActionPreview> PreviewAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken)
    {
        var look = await DockerLook.TakeAsync(context, commands, cancellationToken).ConfigureAwait(false);
        var row = CleanupPreviews.A5Row(look.Snapshot, context.Config, look.Now, testcontainers);
        var selection = Reading.Combine(look.Snapshot.Inventory, look.Snapshot.Details, (inventory, details) =>
            CleanupTargets.StoppedContainers(inventory, details, testcontainers, CleanupPreviews.A5Cutoff(context.Config, look.Now, testcontainers)));
        return RowPreviews.From(row, selection.Map(Items).ValueOr([]), new Dictionary<string, long>(StringComparer.Ordinal));
    }

    /// <summary>Plan §5 A5: any exist.</summary>
    public TriggerDecision Trigger(ActionPreview preview, EffectiveConfig config) =>
        new(preview.Count > 0, string.Create(CultureInfo.InvariantCulture, $"{preview.Count} stopped container(s) past the limit; the trigger is any"));

    public async Task<ActionRun> RunAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken)
    {
        var containers = preview.Targets.Where(t => t.Kind == "container").ToList();
        if (containers.Count == 0)
        {
            return ActionRun.Nothing(commands.Ran, "no container to remove");
        }

        var aliases = containers.ToDictionary(c => c.Key, c => c.Name, StringComparer.Ordinal);
        var removal = await DockerRemovals.RemoveAsync(commands, DockerCleanupCommands.ContainerRemove, containers, aliases, cancellationToken).ConfigureAwait(false);
        var (volumesGone, volumeNote) = await VolumesGoneAsync(commands, preview, removal, cancellationToken).ConfigureAwait(false);
        var removed = removal.Removed.Concat(volumesGone).ToList();
        return new ActionRun(
            removal.Removed.Count,
            removed.Sum(i => i.Bytes ?? 0),
            "the docker system df -v sizes, read just before, of the containers docker rm confirmed and of their anonymous volumes Docker no longer lists",
            null,
            null,
            removed,
            commands.Ran,
            removal.Failure)
        {
            NotRemoved = removal.NotRemoved,
            Notes = [volumeNote],
        };
    }

    /// <summary>Each container a preview item; its anonymous volumes items too, keyed <c>container-id/volume-name</c> so the
    /// run knows whose they are.</summary>
    private static List<ActionItem> Items(ContainerTargets selection) =>
    [
        .. selection.Selected.Select(c => RowPreviews.Item("container", c, string.Create(CultureInfo.InvariantCulture, $"{selection.AnonymousVolumesOf[c.Id].Count} anonymous volume(s)"))),
        .. selection.Selected.SelectMany(c => selection.AnonymousVolumesOf[c.Id].Select(v => VolumeItem(c, v, selection))),
    ];

    private static ActionItem VolumeItem(CleanupTarget container, string volume, ContainerTargets selection)
    {
        var size = selection.AnonymousVolumes.FirstOrDefault(t => t.Id == volume)?.Bytes;
        return new ActionItem("anonymous volume", volume, size is Reading<long>.Available { Value: var b } ? b : null, $"held by {container.Name}") { Key = $"{container.Id}/{volume}" };
    }

    /// <summary>The anonymous volumes of the confirmed containers that Docker no longer lists — and a note when it could not be asked.</summary>
    private static async Task<(IReadOnlyList<ActionItem> Gone, string Note)> VolumesGoneAsync(ActionCommands commands, ActionPreview preview, RemovalResult removal, CancellationToken cancellationToken)
    {
        var confirmed = removal.Removed.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var theirs = preview.Targets.Where(t => t.Kind == "anonymous volume" && confirmed.Contains(t.Key[..t.Key.IndexOf('/', StringComparison.Ordinal)])).ToList();
        if (theirs.Count == 0)
        {
            return ([], "the removed containers held no anonymous volume");
        }

        var listed = DockerCli.Classify(DockerCommands.VolumeList, await commands.RunAsync(DockerCleanupCommands.VolumeListRead, [], cancellationToken).ConfigureAwait(false));
        if (listed is not DockerAnswer.Answered answered)
        {
            return ([], $"their {theirs.Count} anonymous volume(s) are not counted: Docker could not list its volumes ({((DockerAnswer.Failed)listed).Problem.Reason})");
        }

        var still = DanglingVolumes.Parse(answered.Stdout);
        var gone = theirs.Where(v => !still.Contains(v.Name)).ToList();
        return (gone, string.Create(CultureInfo.InvariantCulture, $"{gone.Count} of their {theirs.Count} anonymous volume(s) went with them; {theirs.Count - gone.Count} still listed (another container uses them)"));
    }
}
