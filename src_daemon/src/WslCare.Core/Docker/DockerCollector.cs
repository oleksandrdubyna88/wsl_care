using WslCare.Core.Collectors;

namespace WslCare.Core.Docker;

/// <summary>
/// What Docker answered in one look (plan §4.3) — every part a <see cref="Reading{T}"/>, so a part Docker
/// could not give is unavailable WITH the reason (plan §15b #7), never an empty list or a 0.
/// </summary>
/// <param name="Reachability">Whether a daemon answered at all; when it did not, every part carries its reason.</param>
/// <param name="Totals">Docker's own totals per type (<c>system df</c>).</param>
/// <param name="Inventory">Every image, container, volume and cache entry with its size (<c>system df -v</c>).</param>
/// <param name="Dangling">The volumes no container refers to (<c>volume ls --filter dangling=true</c>).</param>
/// <param name="Details">The inspected fields of every container (<c>container inspect</c> through the template).</param>
public sealed record DockerSnapshot(
    DateTimeOffset SampledAt,
    DockerReachability Reachability,
    Reading<IReadOnlyList<DockerTotal>> Totals,
    Reading<DockerInventory> Inventory,
    Reading<IReadOnlySet<string>> Dangling,
    Reading<IReadOnlyList<ContainerDetail>> Details)
{
    /// <summary>The anonymous volumes no container refers to — what <c>volume-seen.json</c> tracks. Anonymous by Docker's
    /// label (<see cref="AnonymousVolumes"/>), so it needs the inventory too: without it the labels are unknown.</summary>
    public Reading<IReadOnlyList<string>> UnattachedAnonymous =>
        Reading.Combine(Inventory, Dangling, AnonymousVolumes.Unattached);
}

/// <summary>
/// Collects a <see cref="DockerSnapshot"/> through <see cref="DockerCli"/> — read commands only
/// (<see cref="DockerCommands"/>), one after another, each under its own ceiling. The version probe goes
/// first: when no daemon answers, nothing else is started and every part says why.
/// </summary>
public sealed class DockerCollector(DockerCli docker, TimeProvider clock)
{
    public async Task<DockerSnapshot> CollectAsync(CancellationToken cancellationToken)
    {
        var reachability = DockerReachability.From(await docker.RunAsync(DockerCommands.Version, cancellationToken).ConfigureAwait(false));
        if (reachability is DockerReachability.Unreachable { Problem.Reason: var reason })
        {
            return new DockerSnapshot(clock.GetUtcNow(), reachability, Reading.Missing<IReadOnlyList<DockerTotal>>(reason), Reading.Missing<DockerInventory>(reason), Reading.Missing<IReadOnlySet<string>>(reason), Reading.Missing<IReadOnlyList<ContainerDetail>>(reason));
        }

        var totals = Read(await docker.RunAsync(DockerCommands.SystemDf, cancellationToken).ConfigureAwait(false), DockerTotal.Parse);
        var inventory = Read(await docker.RunAsync(DockerCommands.SystemDfVerbose, cancellationToken).ConfigureAwait(false), DockerInventory.Parse);
        var dangling = Read(await docker.RunAsync(DockerCommands.DanglingVolumes, cancellationToken).ConfigureAwait(false), stdout => Reading.Of(DanglingVolumes.Parse(stdout)));
        var details = await DetailsAsync(inventory, cancellationToken).ConfigureAwait(false);
        return new DockerSnapshot(clock.GetUtcNow(), reachability, totals, inventory, dangling, details);
    }

    /// <summary>The inspected fields of every container <c>system df -v</c> listed, in batches; one failed batch
    /// makes the whole part unavailable (a partial list would hide containers from A5).</summary>
    private async Task<Reading<IReadOnlyList<ContainerDetail>>> DetailsAsync(Reading<DockerInventory> inventory, CancellationToken cancellationToken)
    {
        if (inventory is not Reading<DockerInventory>.Available { Value: var listed })
        {
            return Reading.Missing<IReadOnlyList<ContainerDetail>>(inventory.ReasonOrEmpty);
        }

        var details = new List<ContainerDetail>();
        foreach (var batch in listed.Containers.Select(c => c.Id).Chunk(DockerCommands.InspectBatch))
        {
            var answer = await docker.RunAsync(DockerCommands.ContainerInspect(batch), cancellationToken).ConfigureAwait(false);
            if (Read(answer, ContainerDetail.Parse) is not Reading<IReadOnlyList<ContainerDetail>>.Available { Value: var rows })
            {
                return Read(answer, ContainerDetail.Parse);
            }

            details.AddRange(rows);
        }

        return Reading.Of<IReadOnlyList<ContainerDetail>>(details);
    }

    private static Reading<T> Read<T>(DockerAnswer answer, Func<string, Reading<T>> parse) => answer switch
    {
        DockerAnswer.Answered a => parse(a.Stdout),
        DockerAnswer.Failed f => Reading.Missing<T>(f.Problem.Reason),
        _ => throw new System.Diagnostics.UnreachableException("DockerAnswer is a closed set"),
    };
}
