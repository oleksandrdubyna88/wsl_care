using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Docker;
using WslCare.Core.Folders;
using WslCare.Core.Records;

namespace WslCare.Core.Preview;

/// <summary>A sub-figure of a row: what was left out of it, and why (younger than the limit, kept by label, …).</summary>
public sealed record RowNote(string What, int Count, Reading<long> Bytes);

/// <summary>What one cleanup would take: how many objects, the bytes of those whose size Docker reported, how
/// many have no reported size, and the notes.</summary>
public sealed record RowFigures(int Count, long Bytes, int Unsized, IReadOnlyList<RowNote> Notes);

/// <summary>One row of the cleanup table (plan §4.3, §7.2) — the PREVIEW figure of the matching §5 action.</summary>
/// <param name="Id">The row, named as its <c>auto</c> switch is (<c>A4</c>, <c>A5</c>, <c>A5Testcontainers</c>, …).</param>
/// <param name="Action">The §5 action that would do it.</param>
/// <param name="What">The row in words, with the age limit in force.</param>
/// <param name="Basis">Where the bytes come from.</param>
/// <param name="Refusal">Why the action would refuse to run although the row can be counted; empty when it would not.</param>
public sealed record CleanupRow(string Id, string Action, string What, ConfigKey.BoolKey AutoSwitch, bool Auto, string Basis, Reading<RowFigures> Figures, string Refusal);

/// <summary>A named volume no container uses: kept, report-only (plan §4.3).</summary>
public sealed record KeptVolume(string Name, Reading<long> Bytes);

/// <summary>Everything <c>preview --all</c> shows.</summary>
public sealed record CleanupPreview(IReadOnlyList<CleanupRow> Rows, Reading<IReadOnlyList<KeptVolume>> Kept);

/// <summary>
/// The cleanup rows of plan §4.3 computed from one <see cref="DockerSnapshot"/>, the first-sighting record and
/// the age limits in force — PURE, so every row and every limit edge is a unit test. Nothing is removed here;
/// the actions of E3 recompute their targets from live state at run time (plan §15a #0).
/// </summary>
/// <remarks>
/// Every protection the actions will apply is applied here first, so a preview never shows what the action
/// would refuse to take: <c>wsl-care.keep=true</c> on a volume or container, an image any container uses
/// (Docker's count AND the image id of every inspected container — plan §12: "A6 never lists an image a
/// stopped container uses"), named volumes (never a cleanup; the <see cref="CleanupPreview.Kept"/> list).
/// </remarks>
public static class CleanupPreviews
{
    /// <summary>Docker reads a storage cap such as <c>--keep-storage 20GB</c> in binary units.</summary>
    private const long CapUnit = 1L << 30;

    public const string NpmNotYet = "the npm cache size is a folder walk the daily full run takes (plan 4.4); no full run has recorded one yet";
    public const string AptNotYet = "the apt cache and the disabled snap revisions are measured by the daily full run; no full run has recorded them yet";

    /// <summary>The rows without any folder sample — A8 and A9 unavailable with <see cref="NpmNotYet"/> / <see cref="AptNotYet"/>.</summary>
    public static CleanupPreview Build(DockerSnapshot snapshot, VolumeSeenRecord seen, EffectiveConfig config, DateTimeOffset now) =>
        Build(snapshot, seen, config, now, Reading.Missing<AgedPart<FolderSizesSample>>(NpmNotYet));

    /// <summary>The rows; A8 and A9 from the newest folder sample a full run recorded (<paramref name="folders"/>), with its age in the basis.</summary>
    public static CleanupPreview Build(DockerSnapshot snapshot, VolumeSeenRecord seen, EffectiveConfig config, DateTimeOffset now, Reading<AgedPart<FolderSizesSample>> folders)
    {
        var volumeDays = config.Int(ConfigKeys.Volumes.AnonymousOlderThanDays);
        var stoppedDays = config.Int(ConfigKeys.Containers.StoppedOlderThanDays);
        var tcHours = config.Int(ConfigKeys.Containers.TestcontainersOlderThanHours);
        var imageDays = config.Int(ConfigKeys.Images.UnusedOlderThanDays);
        var cacheDays = config.Int(ConfigKeys.BuildCache.OlderThanDays);
        var cacheCapGb = config.Int(ConfigKeys.BuildCache.MaxGb);
        IReadOnlyList<CleanupRow> rows =
        [
            Row("A4", "A4", $"anonymous volumes no container uses, first seen unattached at least {Plural(volumeDays, "day")} ago", ConfigKeys.Auto.A4, config, "volume sizes of docker system df -v",
                Reading.Combine(snapshot.Inventory, snapshot.Dangling, (inventory, dangling) => AnonymousVolumes(inventory, dangling, seen, now.AddDays(-volumeDays))), A4Refusal(snapshot)),
            Row("A5", "A5", $"containers stopped at least {Plural(stoppedDays, "day")} ago, not Testcontainers, with the anonymous volumes they hold", ConfigKeys.Auto.A5, config, "writable layers (system df -v) + their anonymous volumes",
                Reading.Combine(snapshot.Inventory, snapshot.Details, (inventory, details) => StoppedContainers(inventory, details, testcontainers: false, now.AddDays(-stoppedDays))), string.Empty),
            Row("A5Testcontainers", "A5", $"Testcontainers containers stopped at least {Plural(tcHours, "hour")} ago, with their anonymous volumes", ConfigKeys.Auto.A5Testcontainers, config, "writable layers (system df -v) + their anonymous volumes",
                Reading.Combine(snapshot.Inventory, snapshot.Details, (inventory, details) => StoppedContainers(inventory, details, testcontainers: true, now.AddHours(-tcHours))), string.Empty),
            Row("A6", "A6", "dangling images (no name, no tag) no container uses", ConfigKeys.Auto.A6, config, "unique image sizes (system df -v); Docker's images reclaimable is their sum",
                Reading.Combine(snapshot.Inventory, snapshot.Details, (inventory, details) => UnusedImages(inventory, details, dangling: true, DateTimeOffset.MaxValue)), string.Empty),
            Row("A6Unused", "A6", $"tagged images no container uses, created at least {Plural(imageDays, "day")} ago", ConfigKeys.Auto.A6Unused, config, "unique image sizes (system df -v); Docker's images reclaimable is their sum",
                Reading.Combine(snapshot.Inventory, snapshot.Details, (inventory, details) => UnusedImages(inventory, details, dangling: false, now.AddDays(-imageDays))), string.Empty),
            Row("A7", "A7", "build cache neither in use nor shared", ConfigKeys.Auto.A7, config, "build-cache entry sizes (system df -v); Docker's build-cache reclaimable is their sum",
                snapshot.Inventory.Map(inventory => BuildCache(inventory, now.AddDays(-cacheDays), cacheDays, cacheCapGb)), string.Empty),
            Row("A8", "A8", "the npm cache (~/.npm)", ConfigKeys.Auto.A8, config, FolderBasis("folder size", folders), Npm(folders), string.Empty),
            Row("A9", "A9", "the apt cache and disabled snap revisions", ConfigKeys.Auto.A9, config, FolderBasis("folder and snap file sizes", folders), Apt(folders), string.Empty),
        ];
        return new CleanupPreview(rows, Reading.Combine(snapshot.Inventory, snapshot.Dangling, KeptVolumes));
    }

    /// <summary>A8: the size of <c>~/.npm</c>, one object (the cache is cleaned whole), its file count in a note.</summary>
    private static Reading<RowFigures> Npm(Reading<AgedPart<FolderSizesSample>> folders) =>
        Folder(folders, FolderSizes.NpmCache, NpmNotYet).Map(npm => new RowFigures(1, npm.Bytes, 0, [new RowNote(Invariant($"files in the cache{(npm.Complete ? string.Empty : " (walk stopped at its limit: a lower bound)")}"), (int)Math.Min(npm.Files, int.MaxValue), Reading.Of(npm.Bytes))]));

    /// <summary>A9: the apt cache and the disabled snap revisions, each a note; the count is the revisions plus the cache.</summary>
    private static Reading<RowFigures> Apt(Reading<AgedPart<FolderSizesSample>> folders) =>
        Reading.Combine(Folder(folders, FolderSizes.AptCache, AptNotYet), Folder(folders, FolderSizes.SnapDisabled, AptNotYet), (apt, snap) =>
            new RowFigures(1 + (int)snap.Files, apt.Bytes + snap.Bytes, 0, [
                new RowNote("the apt cache (/var/cache/apt)", 1, Reading.Of(apt.Bytes)),
                new RowNote("disabled snap revisions", (int)snap.Files, Reading.Of(snap.Bytes)),
            ]));

    private static Reading<FolderSize> Folder(Reading<AgedPart<FolderSizesSample>> folders, string id, string none) =>
        folders.Bind(part => part.Value.Find(id) switch
        {
            { Measured: true } folder => Reading.Of(folder),
            { } folder => Reading.Missing<FolderSize>($"the full run {part.RunId} could not measure {folder.Path}: {folder.Unavailable}"),
            null => Reading.Missing<FolderSize>(none),
        });

    private static string FolderBasis(string basis, Reading<AgedPart<FolderSizesSample>> folders) => folders switch
    {
        Reading<AgedPart<FolderSizesSample>>.Available { Value: var part } => Invariant($"{basis}, measured by the full run {part.RunId.Text} {part.Age.TotalHours:0.0} h ago"),
        _ => basis,
    };

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static CleanupRow Row(string id, string action, string what, ConfigKey.BoolKey auto, EffectiveConfig config, string basis, Reading<RowFigures> figures, string refusal) =>
        new(id, action, what, auto, config.Bool(auto), basis, figures, refusal);

    private static string A4Refusal(DockerSnapshot snapshot) =>
        snapshot.Reachability is DockerReachability.Reachable { Engine: { ServerMajor: > 0 and < DockerEngine.MinimumMajorForA4 } engine }
            ? $"Docker {engine.ServerVersion} is below {DockerEngine.MinimumMajorForA4}: A4 refuses to run there (plan 5)"
            : string.Empty;

    /// <summary>A4: the dangling list (re-checked by Docker) ∩ anonymous ∖ kept by label, split by first sighting.</summary>
    private static RowFigures AnonymousVolumes(DockerInventory inventory, IReadOnlySet<string> dangling, VolumeSeenRecord seen, DateTimeOffset cutoff)
    {
        var byName = inventory.Volumes.GroupBy(v => v.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var anonymous = dangling.Where(DockerJson.IsFullId).Order(StringComparer.Ordinal).ToList();
        var kept = anonymous.Where(n => byName.TryGetValue(n, out var v) && v.KeptByLabel).ToHashSet(StringComparer.Ordinal);
        var candidates = anonymous.Where(n => !kept.Contains(n)).ToList();
        var old = candidates.Where(n => seen.FirstSeen(n) is Reading<DateTimeOffset>.Available { Value: var first } && first <= cutoff).ToList();
        Reading<long> Size(string name) => byName.TryGetValue(name, out var v) ? v.SizeBytes : Reading.Missing<long>($"{name} is not in docker system df -v");
        return Figures(
            [.. old.Select(Size)],
            [
                Note("first seen unattached more recently than the limit", candidates.Except(old).Select(Size)),
                Note($"protected by the {DockerLabels.Keep}=true label", kept.Select(Size)),
            ]);
    }

    /// <summary>A5: stopped containers (exited, created, dead) of one kind, by <c>FinishedAt</c> (or <c>Created</c>),
    /// with the anonymous volumes they hold; named volumes stay.</summary>
    private static RowFigures StoppedContainers(DockerInventory inventory, IReadOnlyList<ContainerDetail> details, bool testcontainers, DateTimeOffset cutoff)
    {
        var sizes = inventory.Containers.GroupBy(c => c.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().SizeBytes, StringComparer.Ordinal);
        var volumes = inventory.Volumes.GroupBy(v => v.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().SizeBytes, StringComparer.Ordinal);
        var stopped = details.Where(d => d.Stopped && d.Testcontainers == testcontainers).ToList();
        var kept = stopped.Where(d => d.KeptByLabel).ToList();
        var candidates = stopped.Except(kept).ToList();
        var selected = candidates.Where(d => d.StoppedSince is Reading<DateTimeOffset>.Available { Value: var at } && at <= cutoff).ToList();
        var anonymous = selected.SelectMany(d => d.Mounts).Where(m => m.AnonymousVolume).Select(m => m.Name).Distinct(StringComparer.Ordinal).ToList();
        var named = selected.SelectMany(d => d.Mounts).Where(m => m.NamedVolume).Select(m => m.Name).Distinct(StringComparer.Ordinal).ToList();
        Reading<long> Volume(string name) => volumes.TryGetValue(name, out var size) ? size : Reading.Missing<long>($"volume {name} is not in docker system df -v");
        Reading<long> Container(ContainerDetail d) => sizes.TryGetValue(d.Id, out var size) ? size : Reading.Missing<long>($"container {d.Name} is not in docker system df -v");
        var figures = Figures([.. selected.Select(Container)], [
            Note("anonymous volumes they hold (removed with them)", anonymous.Select(Volume)),
            Note("named volumes they hold (kept)", named.Select(Volume)),
            Note("stopped more recently than the limit", candidates.Except(selected).Select(Container)),
            Note($"protected by the {DockerLabels.Keep}=true label", kept.Select(Container)),
        ]);
        var volumeBytes = anonymous.Sum(n => Volume(n).ValueOr(0));
        return figures with { Bytes = figures.Bytes + volumeBytes, Unsized = figures.Unsized + anonymous.Count(n => !Volume(n).IsAvailable) };
    }

    /// <summary>A6: images no container uses — by Docker's count AND by the image id of every inspected container.</summary>
    private static RowFigures UnusedImages(DockerInventory inventory, IReadOnlyList<ContainerDetail> details, bool dangling, DateTimeOffset cutoff)
    {
        var used = details.Select(d => d.ImageId).ToHashSet(StringComparer.Ordinal);
        var unused = inventory.Images.Where(i => i.Dangling == dangling && i.Containers.ValueOr(1) == 0 && !used.Contains(i.Id)).ToList();
        var selected = unused.Where(i => dangling || (i.CreatedAt is Reading<DateTimeOffset>.Available { Value: var at } && at <= cutoff)).ToList();
        return Figures([.. selected.Select(i => i.UniqueBytes)], dangling ? [] : [Note("created more recently than the limit", unused.Except(selected).Select(i => i.UniqueBytes))]);
    }

    /// <summary>A7: what Docker would reclaim, with the two figures plan §5 sets against each other — an age filter
    /// alone (freed 0.02 GB on 2026-10-02) and the size cap.</summary>
    private static RowFigures BuildCache(DockerInventory inventory, DateTimeOffset cutoff, int days, int capGb)
    {
        var reclaimable = inventory.BuildCache.Where(b => b.Reclaimable).ToList();
        var total = inventory.BuildCache.Sum(b => b.SizeBytes.ValueOr(0));
        var aboveCap = Math.Max(0, total - (capGb * CapUnit));
        return Figures([.. reclaimable.Select(b => b.SizeBytes)], [
            Note($"last used more than {Plural(days, "day")} ago (what an age filter alone selects)", reclaimable.Where(b => b.LastUsedAt is Reading<DateTimeOffset>.Available { Value: var at } && at <= cutoff).Select(b => b.SizeBytes)),
            new RowNote($"above the {capGb} GB cap (buildCache.maxGb, what the timer's capped prune frees)", aboveCap > 0 ? 1 : 0, Reading.Of(aboveCap)),
        ]);
    }

    private static IReadOnlyList<KeptVolume> KeptVolumes(DockerInventory inventory, IReadOnlySet<string> dangling) =>
        [.. inventory.Volumes.Where(v => !v.Anonymous && dangling.Contains(v.Name)).Select(v => new KeptVolume(v.Name, v.SizeBytes)).OrderByDescending(v => v.Bytes.ValueOr(0)).ThenBy(v => v.Name, StringComparer.Ordinal)];

    private static RowFigures Figures(IReadOnlyList<Reading<long>> sizes, IReadOnlyList<RowNote> notes) =>
        new(sizes.Count, sizes.Sum(s => s.ValueOr(0)), sizes.Count(s => !s.IsAvailable), notes);

    private static RowNote Note(string what, IEnumerable<Reading<long>> sizes)
    {
        var list = sizes.ToList();
        var unsized = list.FirstOrDefault(s => !s.IsAvailable);
        return new RowNote(what, list.Count, unsized is null ? Reading.Of(list.Sum(s => s.ValueOr(0))) : Reading.Missing<long>(unsized.ReasonOrEmpty));
    }

    private static string Plural(int count, string unit) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {unit}{(count == 1 ? string.Empty : "s")}";
}
