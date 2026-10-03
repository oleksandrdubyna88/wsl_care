using WslCare.Core.Collectors;
using WslCare.Core.Docker;

namespace WslCare.Core.Actions.DockerCleanups;

/// <summary>
/// One LIVE look at Docker for an action (plan §15a #0: every action computes its targets from live state at run time):
/// the snapshot <c>preview --all</c> takes — the same collector, the same read commands, run through the action's own
/// executor — and the first-sighting record observed against it IN MEMORY, exactly as <c>preview</c> observes it.
/// </summary>
/// <param name="Seen">The stored record observed against this snapshot (new volumes first seen now).</param>
/// <param name="SeenProblem">Why the stored record could not be read, when it could not (it is then empty — which can only
/// make A4 select FEWER: every volume counts as first seen now).</param>
public sealed record DockerLook(DockerSnapshot Snapshot, VolumeSeenRecord Seen, string SeenProblem)
{
    /// <summary>The look, through the action's executor (<see cref="ActionCommands.AsRunner"/>: only declared reads run).</summary>
    public static async Task<DockerLook> TakeAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken)
    {
        var snapshot = await new DockerCollector(new DockerCli(commands.AsRunner()), context.Clock).CollectAsync(cancellationToken).ConfigureAwait(false);
        var load = new VolumeSeenStore(context.Paths, context.Files).Read();
        var seen = snapshot.UnattachedAnonymous is Reading<IReadOnlyList<string>>.Available { Value: var names } ? load.Record.Observe(names, snapshot.SampledAt) : load.Record;
        return new DockerLook(snapshot, seen, load.Problem);
    }

    /// <summary>The instant the row's limits are measured from — the snapshot's, as <c>preview --all</c> uses.</summary>
    public DateTimeOffset Now => Snapshot.SampledAt;
}
