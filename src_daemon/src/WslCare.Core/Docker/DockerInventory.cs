using System.Text.Json;

using WslCare.Core.Collectors;

namespace WslCare.Core.Docker;

/// <summary>One line of <c>docker system df</c>: Docker's own totals for one type.</summary>
/// <param name="Type"><c>Images</c>, <c>Containers</c>, <c>Local Volumes</c> or <c>Build Cache</c>, as Docker names it.</param>
public sealed record DockerTotal(string Type, Reading<int> TotalCount, Reading<int> Active, Reading<long> SizeBytes, Reading<long> ReclaimableBytes)
{
    public const string Images = "Images";
    public const string Containers = "Containers";
    public const string Volumes = "Local Volumes";
    public const string BuildCache = "Build Cache";

    /// <summary>Every line of <see cref="DockerCommands.SystemDf"/>.</summary>
    public static Reading<IReadOnlyList<DockerTotal>> Parse(string stdout) =>
        DockerJson.Lines(stdout, "docker system df", row => new DockerTotal(
            DockerText.Field(row, "Type"),
            DockerJson.Count(DockerText.Field(row, "TotalCount")),
            DockerJson.Count(DockerText.Field(row, "Active")),
            DockerText.Bytes(DockerText.Field(row, "Size")),
            DockerText.Bytes(DockerText.Field(row, "Reclaimable"))));
}

/// <summary>An image of <c>docker system df -v</c>.</summary>
/// <param name="Containers">How many containers — running or not — use it. Docker's figure.</param>
/// <param name="UniqueBytes">What removing it frees: its size minus the layers it shares. Docker's images
/// "reclaimable" is the sum of this over the images no container uses (checked by hand on 2026-10-02:
/// 9.806 GB both ways, and held by the live contract).</param>
public sealed record ImageRow(
    string Id,
    string Repository,
    string Tag,
    Reading<int> Containers,
    Reading<DateTimeOffset> CreatedAt,
    Reading<long> SizeBytes,
    Reading<long> UniqueBytes)
{
    /// <summary>No name and no tag: a superseded build layer (<c>docker image prune</c>'s target).</summary>
    public bool Dangling => Repository == "<none>";

    /// <summary>The name a person reads: <c>repository:tag</c>, or the short id of a dangling image.</summary>
    public string Display => Dangling ? ShortId(Id) : $"{Repository}:{Tag}";

    private static string ShortId(string id) => id.StartsWith("sha256:", StringComparison.Ordinal) && id.Length >= 19 ? id[7..19] : id;
}

/// <summary>A container of <c>docker system df -v</c> (the same row <c>docker ps</c> prints).</summary>
/// <param name="SizeBytes">Its writable layer — what <c>docker rm</c> frees besides its anonymous volumes.</param>
public sealed record ContainerRow(
    string Id,
    string Name,
    string Image,
    string State,
    Reading<DateTimeOffset> CreatedAt,
    Reading<long> SizeBytes,
    IReadOnlyDictionary<string, string> Labels)
{
    /// <summary>A row of <c>system df -v</c> or of <c>docker ps --format {{json .}}</c>.</summary>
    public static ContainerRow From(JsonElement row) =>
        new(
            DockerText.Field(row, "ID"),
            DockerText.Field(row, "Names"),
            DockerText.Field(row, "Image"),
            DockerText.Field(row, "State"),
            DockerText.Instant(DockerText.Field(row, "CreatedAt")),
            DockerText.Bytes(DockerText.Field(row, "Size")),
            DockerJson.Labels(DockerText.Field(row, "Labels")));
}

/// <summary>A volume of <c>docker system df -v</c>.</summary>
/// <param name="Links">How many containers refer to it, running or not.</param>
public sealed record VolumeRow(string Name, Reading<int> Links, Reading<long> SizeBytes, IReadOnlyDictionary<string, string> Labels)
{
    /// <summary>Plan §4.3, as Docker itself decides it: an anonymous volume carries Docker's
    /// <c>com.docker.volume.anonymous</c> label AND a 64-hex name. The name alone is not enough — <c>docker volume
    /// create</c> without a name also gets a random 64-hex name but NO label, and since Docker 23 <c>volume prune</c>
    /// (without <c>--all</c>) keeps it as NAMED; taking it would be <c>volume prune --all</c> (independent review of E3,
    /// 2026-10-03). On 2026-10-02 the label and the name agreed on all 48 volumes.</summary>
    public bool Anonymous => DockerJson.IsFullId(Name) && Labels.ContainsKey(DockerLabels.Anonymous);

    /// <summary>Plan §5: <c>wsl-care.keep=true</c> protects a volume from every action.</summary>
    public bool KeptByLabel => Labels.TryGetValue(DockerLabels.Keep, out var value) && value == "true";
}

/// <summary>A build-cache entry of <c>docker system df -v</c>.</summary>
public sealed record BuildCacheRow(string Id, bool InUse, bool Shared, Reading<DateTimeOffset> LastUsedAt, Reading<long> SizeBytes)
{
    /// <summary>What <c>docker builder prune</c> may take: not in use and not shared with another entry —
    /// the figure Docker's own "reclaimable" adds up (23.98 MB of 1.007 GB on 2026-10-02, by hand both ways).</summary>
    public bool Reclaimable => !InUse && !Shared;
}

/// <summary>Everything <c>docker system df -v</c> lists.</summary>
public sealed record DockerInventory(
    IReadOnlyList<ImageRow> Images,
    IReadOnlyList<ContainerRow> Containers,
    IReadOnlyList<VolumeRow> Volumes,
    IReadOnlyList<BuildCacheRow> BuildCache)
{
    /// <summary>The answer of <see cref="DockerCommands.SystemDfVerbose"/>.</summary>
    public static Reading<DockerInventory> Parse(string stdout) =>
        DockerJson.Document(stdout, "docker system df -v", root => new DockerInventory(
            DockerJson.Array(root, "Images", Image),
            DockerJson.Array(root, "Containers", ContainerRow.From),
            DockerJson.Array(root, "Volumes", Volume),
            DockerJson.Array(root, "BuildCache", Cache)));

    private static ImageRow Image(JsonElement row) =>
        new(
            DockerText.Field(row, "ID"),
            DockerText.Field(row, "Repository"),
            DockerText.Field(row, "Tag"),
            DockerJson.Count(DockerText.Field(row, "Containers")),
            DockerText.Instant(DockerText.Field(row, "CreatedAt")),
            DockerText.Bytes(DockerText.Field(row, "Size")),
            DockerText.Bytes(DockerText.Field(row, "UniqueSize")));

    private static VolumeRow Volume(JsonElement row) =>
        new(
            DockerText.Field(row, "Name"),
            DockerJson.Count(DockerText.Field(row, "Links")),
            DockerText.Bytes(DockerText.Field(row, "Size")),
            DockerJson.Labels(DockerText.Field(row, "Labels")));

    private static BuildCacheRow Cache(JsonElement row) =>
        new(
            DockerText.Field(row, "ID"),
            DockerText.Field(row, "InUse") == "true",
            DockerText.Field(row, "Shared") == "true",
            DockerText.Instant(DockerText.Field(row, "LastUsedAt")),
            DockerText.Bytes(DockerText.Field(row, "Size")));
}

/// <summary>The labels the daemon reads (plan §4.3, §5).</summary>
public static class DockerLabels
{
    /// <summary>Protects a volume or a container from every action.</summary>
    public const string Keep = "wsl-care.keep";

    /// <summary>What Testcontainers puts on everything it starts.</summary>
    public const string Testcontainers = "org.testcontainers";

    /// <summary>What Docker (23+) puts on a volume it created anonymously — the one mark <c>volume prune</c> without
    /// <c>--all</c> removes by.</summary>
    public const string Anonymous = "com.docker.volume.anonymous";
}

/// <summary>Which volumes are anonymous by <see cref="VolumeRow.Anonymous"/> — the ONE rule A4's selection, its first
/// sightings (<c>volume-seen.json</c>) and A5's accounting share. A volume whose labels are unknown (missing from
/// <c>system df -v</c>) is never anonymous here.</summary>
public static class AnonymousVolumes
{
    /// <summary>The volumes of <paramref name="inventory"/> by name (the first row of a repeated name).</summary>
    public static IReadOnlyDictionary<string, VolumeRow> ByName(DockerInventory inventory) =>
        inventory.Volumes.GroupBy(v => v.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    /// <summary>The anonymous volumes no container refers to: in the dangling list AND anonymous in the inventory.</summary>
    public static IReadOnlyList<string> Unattached(DockerInventory inventory, IReadOnlySet<string> dangling)
    {
        var byName = ByName(inventory);
        return [.. dangling.Where(n => IsAnonymous(byName, n)).Order(StringComparer.Ordinal)];
    }

    /// <summary>Whether <paramref name="name"/> is a listed volume carrying Docker's anonymous label and a 64-hex name.</summary>
    public static bool IsAnonymous(IReadOnlyDictionary<string, VolumeRow> byName, string name) =>
        byName.TryGetValue(name, out var volume) && volume.Anonymous;
}

/// <summary>The names <c>docker volume ls --filter dangling=true</c> printed: volumes no container refers to.</summary>
public static class DanglingVolumes
{
    public static IReadOnlySet<string> Parse(string stdout) =>
        stdout.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet(StringComparer.Ordinal);
}
