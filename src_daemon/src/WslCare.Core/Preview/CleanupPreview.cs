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
/// the age limits in force — PURE, so every row and every limit edge is a unit test. Nothing is removed here.
/// </summary>
/// <remarks>
/// <para>Each row is its own public builder (<see cref="A4Row"/> … <see cref="A9Row"/>) over the selections of
/// <see cref="CleanupTargets"/>, and the actions of E3.S2 build their previews by calling the SAME builder over a LIVE
/// look — so <c>act &lt;A#&gt; --preview</c> and the row of <c>preview --all</c> are one computation, not two that agree.</para>
/// <para>Every protection the actions apply is applied in the selection first, so a preview never shows what the action
/// would refuse to take: <c>wsl-care.keep=true</c> on a volume or container, an image any container uses (Docker's count
/// AND the image id of every inspected container — plan §12: "A6 never lists an image a stopped container uses"), named
/// volumes (never a cleanup; the <see cref="CleanupPreview.Kept"/> list).</para>
/// </remarks>
public static class CleanupPreviews
{
    public const string NpmNotYet = "the npm cache size is a folder walk the daily full run takes (plan 4.4); no full run has recorded one yet";
    public const string AptNotYet = "the apt cache and the disabled snap revisions are measured by the daily full run; no full run has recorded them yet";

    /// <summary>The A4 refusal when Docker's version could not be read as a number (E3.S2: unknown is not "new enough").</summary>
    public const string A4VersionUnknown = "Docker's server version could not be read as a number: A4 refuses where it cannot tell Docker 23 or later (plan 5)";

    /// <summary>The rows without any folder sample — A8 and A9 unavailable with <see cref="NpmNotYet"/> / <see cref="AptNotYet"/>.</summary>
    public static CleanupPreview Build(DockerSnapshot snapshot, VolumeSeenRecord seen, EffectiveConfig config, DateTimeOffset now) =>
        Build(snapshot, seen, config, now, Reading.Missing<AgedPart<FolderSizesSample>>(NpmNotYet));

    /// <summary>The rows; A8 and A9 from the newest folder sample a full run recorded (<paramref name="folders"/>), with its age in the basis.</summary>
    public static CleanupPreview Build(DockerSnapshot snapshot, VolumeSeenRecord seen, EffectiveConfig config, DateTimeOffset now, Reading<AgedPart<FolderSizesSample>> folders)
    {
        IReadOnlyList<CleanupRow> rows =
        [
            A4Row(snapshot, seen, config, now),
            A5Row(snapshot, config, now, testcontainers: false),
            A5Row(snapshot, config, now, testcontainers: true),
            A6Row(snapshot, config, now, unused: false),
            A6Row(snapshot, config, now, unused: true),
            A7Row(snapshot, config, now),
            A8Row(config, folders),
            A9Row(config, folders),
        ];
        return new CleanupPreview(rows, Reading.Combine(snapshot.Inventory, snapshot.Dangling, KeptVolumes));
    }

    /// <summary>A4's cutoff: first seen unattached at least <c>volumes.anonymousOlderThanDays</c> before <paramref name="now"/>.</summary>
    public static DateTimeOffset A4Cutoff(EffectiveConfig config, DateTimeOffset now) => now.AddDays(-config.Int(ConfigKeys.Volumes.AnonymousOlderThanDays));

    public static DateTimeOffset A5Cutoff(EffectiveConfig config, DateTimeOffset now, bool testcontainers) =>
        testcontainers ? now.AddHours(-config.Int(ConfigKeys.Containers.TestcontainersOlderThanHours)) : now.AddDays(-config.Int(ConfigKeys.Containers.StoppedOlderThanDays));

    public static DateTimeOffset A6Cutoff(EffectiveConfig config, DateTimeOffset now, bool unused) =>
        unused ? now.AddDays(-config.Int(ConfigKeys.Images.UnusedOlderThanDays)) : DateTimeOffset.MaxValue;

    public static CleanupRow A4Row(DockerSnapshot snapshot, VolumeSeenRecord seen, EffectiveConfig config, DateTimeOffset now) =>
        Row("A4", "A4", $"anonymous volumes no container uses, first seen unattached at least {Plural(config.Int(ConfigKeys.Volumes.AnonymousOlderThanDays), "day")} ago", ConfigKeys.Auto.A4, config, "volume sizes of docker system df -v",
            Reading.Combine(snapshot.Inventory, snapshot.Dangling, (inventory, dangling) => VolumeFigures(CleanupTargets.AnonymousVolumes(inventory, dangling, seen, A4Cutoff(config, now)))), A4Refusal(snapshot));

    public static CleanupRow A5Row(DockerSnapshot snapshot, EffectiveConfig config, DateTimeOffset now, bool testcontainers) =>
        testcontainers
            ? Row("A5Testcontainers", "A5", $"Testcontainers containers stopped at least {Plural(config.Int(ConfigKeys.Containers.TestcontainersOlderThanHours), "hour")} ago, with their anonymous volumes", ConfigKeys.Auto.A5Testcontainers, config, ContainerBasis,
                Reading.Combine(snapshot.Inventory, snapshot.Details, (inventory, details) => ContainerFigures(CleanupTargets.StoppedContainers(inventory, details, testcontainers: true, A5Cutoff(config, now, true)))), string.Empty)
            : Row("A5", "A5", $"containers stopped at least {Plural(config.Int(ConfigKeys.Containers.StoppedOlderThanDays), "day")} ago, not Testcontainers, with the anonymous volumes they hold", ConfigKeys.Auto.A5, config, ContainerBasis,
                Reading.Combine(snapshot.Inventory, snapshot.Details, (inventory, details) => ContainerFigures(CleanupTargets.StoppedContainers(inventory, details, testcontainers: false, A5Cutoff(config, now, false)))), string.Empty);

    public static CleanupRow A6Row(DockerSnapshot snapshot, EffectiveConfig config, DateTimeOffset now, bool unused) =>
        unused
            ? Row("A6Unused", "A6", $"tagged images no container uses, created at least {Plural(config.Int(ConfigKeys.Images.UnusedOlderThanDays), "day")} ago", ConfigKeys.Auto.A6Unused, config, ImageBasis,
                Reading.Combine(snapshot.Inventory, snapshot.Details, (inventory, details) => ImageFigures(CleanupTargets.UnusedImages(inventory, details, dangling: false, A6Cutoff(config, now, true)), withNote: true)), string.Empty)
            : Row("A6", "A6", "dangling images (no name, no tag) no container uses", ConfigKeys.Auto.A6, config, ImageBasis,
                Reading.Combine(snapshot.Inventory, snapshot.Details, (inventory, details) => ImageFigures(CleanupTargets.UnusedImages(inventory, details, dangling: true, A6Cutoff(config, now, false)), withNote: false)), string.Empty);

    public static CleanupRow A7Row(DockerSnapshot snapshot, EffectiveConfig config, DateTimeOffset now)
    {
        var days = config.Int(ConfigKeys.BuildCache.OlderThanDays);
        var capGb = config.Int(ConfigKeys.BuildCache.MaxGb);
        return Row("A7", "A7", "build cache neither in use nor shared", ConfigKeys.Auto.A7, config, "build-cache entry sizes (system df -v); Docker's build-cache reclaimable is their sum",
            snapshot.Inventory.Map(inventory => CacheFigures(CleanupTargets.BuildCache(inventory, now.AddDays(-days), capGb), days, capGb)), string.Empty);
    }

    public static CleanupRow A8Row(EffectiveConfig config, Reading<AgedPart<FolderSizesSample>> folders) =>
        Row("A8", "A8", "the npm cache (~/.npm)", ConfigKeys.Auto.A8, config, FolderBasis("folder size", folders), Npm(folders), string.Empty);

    public static CleanupRow A9Row(EffectiveConfig config, Reading<AgedPart<FolderSizesSample>> folders) =>
        Row("A9", "A9", "the apt cache and disabled snap revisions", ConfigKeys.Auto.A9, config, FolderBasis("folder and snap file sizes", folders), Apt(folders), string.Empty);

    /// <summary>The A4 refusal for this snapshot: Docker below 23, or a version that is not a number; empty otherwise
    /// (including when no daemon answered — the row is unavailable then, and so is the action).</summary>
    public static string A4Refusal(DockerSnapshot snapshot) => snapshot.Reachability switch
    {
        DockerReachability.Reachable { Engine.ServerMajor: 0 } => A4VersionUnknown,
        DockerReachability.Reachable { Engine: { ServerMajor: < DockerEngine.MinimumMajorForA4 } engine } =>
            $"Docker {engine.ServerVersion} is below {DockerEngine.MinimumMajorForA4}: A4 refuses to run there (plan 5)",
        _ => string.Empty,
    };

    private const string ContainerBasis = "writable layers (system df -v) + their anonymous volumes";
    private const string ImageBasis = "unique image sizes (system df -v); Docker's images reclaimable is their sum";

    /// <summary>A8: the size of <c>~/.npm</c>, one object (the cache is cleaned whole), its file count in a note.</summary>
    private static Reading<RowFigures> Npm(Reading<AgedPart<FolderSizesSample>> folders) =>
        Folder(folders, FolderSizes.NpmCache, NpmNotYet).Map(npm => new RowFigures(1, npm.Bytes, 0, [new RowNote(Invariant($"files in the cache{(npm.Complete ? string.Empty : " (walk stopped at its limit: a lower bound)")}"), (int)Math.Min(npm.Files, int.MaxValue), Reading.Of(npm.Bytes))]));

    /// <summary>A9: the apt cache and the disabled snap revisions, each a note; the count is the revisions plus the cache.</summary>
    private static Reading<RowFigures> Apt(Reading<AgedPart<FolderSizesSample>> folders) =>
        Reading.Combine(Folder(folders, FolderSizes.AptCache, AptNotYet), Folder(folders, FolderSizes.SnapDisabled, AptNotYet), (apt, snap) =>
            new RowFigures(1 + (int)snap.Files, apt.Bytes + snap.Bytes, 0, [
                new RowNote(AptCacheNote, 1, Reading.Of(apt.Bytes)),
                new RowNote(SnapRevisionsNote, (int)snap.Files, Reading.Of(snap.Bytes)),
            ]));

    /// <summary>The A9 note naming the apt cache — what A9's trigger reads back.</summary>
    public const string AptCacheNote = "the apt cache (/var/cache/apt)";

    /// <summary>The A9 note naming the disabled snap revisions — what A9's trigger reads back.</summary>
    public const string SnapRevisionsNote = "disabled snap revisions";

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

    private static RowFigures VolumeFigures(VolumeTargets targets) =>
        Figures([.. targets.Selected.Select(t => t.Bytes)],
        [
            Note("first seen unattached more recently than the limit", targets.Younger.Select(t => t.Bytes)),
            Note($"protected by the {DockerLabels.Keep}=true label", targets.Kept.Select(t => t.Bytes)),
            Note($"64-hex names without Docker's anonymous label ({DockerLabels.Anonymous}): named to Docker, kept and listed with the named volumes", targets.NamedHex.Select(t => t.Bytes)),
            Note("unattached volumes whose labels are unknown (not in docker system df -v): never selected", targets.LabelsUnknown.Select(t => t.Bytes)),
        ]);

    private static RowFigures ContainerFigures(ContainerTargets targets)
    {
        var figures = Figures([.. targets.Selected.Select(t => t.Bytes)],
        [
            Note("anonymous volumes they hold (removed with them)", targets.AnonymousVolumes.Select(t => t.Bytes)),
            Note("named volumes they hold (kept)", targets.NamedVolumes.Select(t => t.Bytes)),
            Note("volumes they hold whose labels are unknown (not in docker system df -v): not counted", targets.UnknownVolumes.Select(t => t.Bytes)),
            Note("stopped more recently than the limit", targets.Younger.Select(t => t.Bytes)),
            Note($"protected by the {DockerLabels.Keep}=true label", targets.Kept.Select(t => t.Bytes)),
        ]);
        var volumeBytes = targets.AnonymousVolumes.Sum(v => v.Bytes.ValueOr(0));
        return figures with { Bytes = figures.Bytes + volumeBytes, Unsized = figures.Unsized + targets.AnonymousVolumes.Count(v => !v.Bytes.IsAvailable) };
    }

    private static RowFigures ImageFigures(ImageTargets targets, bool withNote) =>
        Figures([.. targets.Selected.Select(t => t.Bytes)], withNote ? [Note("created more recently than the limit", targets.Younger.Select(t => t.Bytes))] : []);

    private static RowFigures CacheFigures(CacheTargets targets, int days, int capGb) =>
        Figures([.. targets.Selected.Select(t => t.Bytes)],
        [
            Note($"last used more than {Plural(days, "day")} ago (what an age filter alone selects)", targets.Aged.Select(t => t.Bytes)),
            new RowNote($"above the {capGb} GB cap (buildCache.maxGb, what the timer's capped prune frees)", targets.AboveCapBytes > 0 ? 1 : 0, Reading.Of(targets.AboveCapBytes)),
        ]);

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

    internal static string Plural(int count, string unit) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {unit}{(count == 1 ? string.Empty : "s")}";
}
