using WslCare.Core.Collectors;
using WslCare.Core.Docker;

namespace WslCare.Core.Preview;

/// <summary>One Docker object a cleanup row selected: its full id (what a command names it by), the name a person reads,
/// and its size as Docker reported it.</summary>
public sealed record CleanupTarget(string Id, string Name, Reading<long> Bytes);

/// <summary>A4's selection: the anonymous volumes old enough to go, those first seen too recently, those the keep label
/// protects.</summary>
public sealed record VolumeTargets(IReadOnlyList<CleanupTarget> Selected, IReadOnlyList<CleanupTarget> Younger, IReadOnlyList<CleanupTarget> Kept);

/// <summary>A5's selection: the stopped containers old enough to go and, per container, the anonymous volumes it holds
/// (removed with it by <c>docker rm -v</c>); the named volumes they hold (kept), the younger ones, the labelled ones.</summary>
public sealed record ContainerTargets(
    IReadOnlyList<CleanupTarget> Selected,
    IReadOnlyDictionary<string, IReadOnlyList<string>> AnonymousVolumesOf,
    IReadOnlyList<CleanupTarget> AnonymousVolumes,
    IReadOnlyList<CleanupTarget> NamedVolumes,
    IReadOnlyList<CleanupTarget> Younger,
    IReadOnlyList<CleanupTarget> Kept);

/// <summary>A6's selection: the images no container uses (of one kind), and those created too recently.</summary>
public sealed record ImageTargets(IReadOnlyList<CleanupTarget> Selected, IReadOnlyList<CleanupTarget> Younger);

/// <summary>A7's selection: the reclaimable cache entries, those an age filter alone would take, the bytes above the cap.</summary>
public sealed record CacheTargets(IReadOnlyList<CleanupTarget> Selected, IReadOnlyList<CleanupTarget> Aged, long TotalBytes, long AboveCapBytes);

/// <summary>
/// WHICH Docker objects each cleanup row of plan §4.3 selects — pure, and the ONE place the selection is made: the row's
/// figures (<see cref="CleanupPreviews"/>, what <c>preview --all</c> shows) and the action's targets (E3.S2: what
/// <c>act</c> removes) are both computed from these lists, so an action's preview cannot differ from its row.
/// </summary>
/// <remarks>Every protection is applied HERE, before either reader sees a list: the <c>wsl-care.keep=true</c> label on a
/// volume or a container, an image any container uses (Docker's count AND every inspected container's image id), a named
/// volume (never A4's; kept with a container A5 removes).</remarks>
public static class CleanupTargets
{
    /// <summary>Docker reads a storage cap such as <c>--max-used-space 20GB</c> in binary units.</summary>
    public const long CapUnit = 1L << 30;

    /// <summary>A4: the dangling list (re-checked by Docker) ∩ anonymous ∖ kept by label, split by first sighting.</summary>
    public static VolumeTargets AnonymousVolumes(DockerInventory inventory, IReadOnlySet<string> dangling, VolumeSeenRecord seen, DateTimeOffset cutoff)
    {
        var byName = ByName(inventory);
        var anonymous = dangling.Where(DockerJson.IsFullId).Order(StringComparer.Ordinal).ToList();
        var kept = anonymous.Where(n => byName.TryGetValue(n, out var v) && v.KeptByLabel).ToHashSet(StringComparer.Ordinal);
        var candidates = anonymous.Where(n => !kept.Contains(n)).ToList();
        var old = candidates.Where(n => seen.FirstSeen(n) is Reading<DateTimeOffset>.Available { Value: var first } && first <= cutoff).ToList();
        CleanupTarget Volume(string name) => new(name, name, VolumeSize(byName, name));
        return new VolumeTargets([.. old.Select(Volume)], [.. candidates.Except(old).Select(Volume)], [.. kept.Order(StringComparer.Ordinal).Select(Volume)]);
    }

    /// <summary>A5: stopped containers (exited, created, dead) of one kind, by <c>FinishedAt</c> (or <c>Created</c>), with
    /// the anonymous volumes they hold; named volumes stay.</summary>
    public static ContainerTargets StoppedContainers(DockerInventory inventory, IReadOnlyList<ContainerDetail> details, bool testcontainers, DateTimeOffset cutoff)
    {
        var sizes = inventory.Containers.GroupBy(c => c.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().SizeBytes, StringComparer.Ordinal);
        var volumes = ByName(inventory);
        var stopped = details.Where(d => d.Stopped && d.Testcontainers == testcontainers).ToList();
        var kept = stopped.Where(d => d.KeptByLabel).ToList();
        var candidates = stopped.Except(kept).ToList();
        var selected = candidates.Where(d => d.StoppedSince is Reading<DateTimeOffset>.Available { Value: var at } && at <= cutoff).ToList();
        var anonymous = selected.SelectMany(d => d.Mounts).Where(m => m.AnonymousVolume).Select(m => m.Name).Distinct(StringComparer.Ordinal).ToList();
        var named = selected.SelectMany(d => d.Mounts).Where(m => m.NamedVolume).Select(m => m.Name).Distinct(StringComparer.Ordinal).ToList();
        CleanupTarget Container(ContainerDetail d) => new(d.Id, d.Name, sizes.TryGetValue(d.Id, out var size) ? size : Reading.Missing<long>($"container {d.Name} is not in docker system df -v"));
        CleanupTarget Volume(string name) => new(name, name, volumes.TryGetValue(name, out var v) ? v.SizeBytes : Reading.Missing<long>($"volume {name} is not in docker system df -v"));
        return new ContainerTargets(
            [.. selected.Select(Container)],
            selected.ToDictionary(d => d.Id, d => (IReadOnlyList<string>)[.. d.Mounts.Where(m => m.AnonymousVolume).Select(m => m.Name).Distinct(StringComparer.Ordinal)], StringComparer.Ordinal),
            [.. anonymous.Select(Volume)],
            [.. named.Select(Volume)],
            [.. candidates.Except(selected).Select(Container)],
            [.. kept.Select(Container)]);
    }

    /// <summary>A6: images no container uses — by Docker's count AND by the image id of every inspected container.</summary>
    public static ImageTargets UnusedImages(DockerInventory inventory, IReadOnlyList<ContainerDetail> details, bool dangling, DateTimeOffset cutoff)
    {
        var used = details.Select(d => d.ImageId).ToHashSet(StringComparer.Ordinal);
        var unused = inventory.Images.Where(i => i.Dangling == dangling && i.Containers.ValueOr(1) == 0 && !used.Contains(i.Id)).ToList();
        var selected = unused.Where(i => dangling || (i.CreatedAt is Reading<DateTimeOffset>.Available { Value: var at } && at <= cutoff)).ToList();
        static CleanupTarget Image(ImageRow i) => new(i.Id, i.Display, i.UniqueBytes);
        return new ImageTargets([.. selected.Select(Image)], [.. unused.Except(selected).Select(Image)]);
    }

    /// <summary>A7: what Docker would reclaim, with the two figures plan §5 sets against each other — an age filter alone
    /// (freed 0.02 GB on 2026-10-02) and the size cap.</summary>
    public static CacheTargets BuildCache(DockerInventory inventory, DateTimeOffset cutoff, int capGb)
    {
        var reclaimable = inventory.BuildCache.Where(b => b.Reclaimable).ToList();
        var total = inventory.BuildCache.Sum(b => b.SizeBytes.ValueOr(0));
        static CleanupTarget Entry(BuildCacheRow b) => new(b.Id, b.Id, b.SizeBytes);
        return new CacheTargets(
            [.. reclaimable.Select(Entry)],
            [.. reclaimable.Where(b => b.LastUsedAt is Reading<DateTimeOffset>.Available { Value: var at } && at <= cutoff).Select(Entry)],
            total,
            Math.Max(0, total - (capGb * CapUnit)));
    }

    private static Dictionary<string, VolumeRow> ByName(DockerInventory inventory) =>
        inventory.Volumes.GroupBy(v => v.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    private static Reading<long> VolumeSize(Dictionary<string, VolumeRow> byName, string name) =>
        byName.TryGetValue(name, out var v) ? v.SizeBytes : Reading.Missing<long>($"{name} is not in docker system df -v");
}
