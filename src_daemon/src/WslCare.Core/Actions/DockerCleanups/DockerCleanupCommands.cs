using WslCare.Core.Collectors;
using WslCare.Core.Docker;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;

namespace WslCare.Core.Actions.DockerCleanups;

/// <summary>
/// Every Docker command the cleanup actions A4–A7 may run (plan §5, §15 #4), as DECLARED templates — the WRITE half next
/// to the collectors' reads (<see cref="DockerCommands"/>, which holds read verbs only and is tested to). No template
/// takes <c>-f</c> on a removal: <c>docker volume rm</c> refuses a volume in use and <c>docker rm</c> a running
/// container, a second check by Docker itself on every target. Nothing here can name <c>system prune</c> or
/// <c>volume prune</c> (the never-list refuses both, in any form).
/// </summary>
public static class DockerCleanupCommands
{
    /// <summary>How many names one removal takes: 100 full ids are 6.5 k characters, far below a command line's limit —
    /// the same batch the inspect uses.</summary>
    public const int Batch = DockerCommands.InspectBatch;

    /// <summary>The label that protects a volume or a container from every action (plan §5) — and, given to
    /// <c>image prune</c> as a filter, an image as well (E3.S2).</summary>
    public const string KeepFilter = "label!=" + DockerLabels.Keep + "=true";

    /// <summary>A removal of up to <see cref="Batch"/> objects: Docker answers per object.</summary>
    public static readonly TimeSpan RemovalCeiling = TimeSpan.FromMinutes(5);

    /// <summary>A prune walks every image or cache entry: minutes on a large store.</summary>
    public static readonly TimeSpan PruneCeiling = TimeSpan.FromMinutes(15);

    private const int OutputCap = 4 * 1024 * 1024;

    private static readonly SlotKind.Hex FullId = new(64);

    /// <summary>A4: <c>docker volume rm &lt;64-hex&gt;…</c> — only an ANONYMOUS volume's name has that shape; a named volume
    /// cannot be put in the slot at all.</summary>
    public static readonly CommandTemplate VolumeRemove = Machine("docker-volume-rm", [L("volume"), L("rm"), new ArgPart.Repeat("volume", FullId, 1, Batch)], RemovalCeiling);

    /// <summary>A5: <c>docker rm -v &lt;64-hex id&gt;…</c> — the container and the ANONYMOUS volumes it holds; never <c>-f</c>.</summary>
    public static readonly CommandTemplate ContainerRemove = Machine("docker-rm-v", [L("rm"), L("-v"), new ArgPart.Repeat("container", FullId, 1, Batch)], RemovalCeiling);

    /// <summary>A6: dangling images, never one a container uses (Docker's own rule) nor one carrying the keep label.</summary>
    public static readonly CommandTemplate ImagePruneDangling = Machine("docker-image-prune-dangling", [L("image"), L("prune"), L("-f"), L("--filter"), L(KeepFilter)], PruneCeiling);

    /// <summary>A6Unused: every image no container uses, created at least the limit ago (<c>until=&lt;hours&gt;h</c>).</summary>
    public static readonly CommandTemplate ImagePruneUnused = Machine(
        "docker-image-prune-unused",
        [L("image"), L("prune"), L("-a"), L("-f"), L("--filter"), new ArgPart.Slot("until", new SlotKind.Prefixed("until=", new SlotKind.Number(0, 87_600, "h"))), L("--filter"), L(KeepFilter)],
        PruneCeiling);

    /// <summary>A7's detection (read-only): which size cap THIS Docker's builder prune takes — "detect, don't guess".</summary>
    public static readonly ToolCommand BuilderPruneHelpCommand = new(DockerCommands.Executable, "docker-builder-prune-help", ["builder", "prune", "--help"], DockerCommands.ProbeCeiling, 1024 * 1024);

    /// <summary><see cref="BuilderPruneHelpCommand"/> as a template (every argument a literal).</summary>
    public static readonly CommandTemplate BuilderPruneHelp = CommandTemplate.Fixed(BuilderPruneHelpCommand);

    /// <summary>A7, the timer: prune until the cache holds at most the cap (buildx: <c>--max-used-space</c>).</summary>
    public static readonly CommandTemplate BuilderPruneMaxUsed = Machine("docker-builder-prune-max-used-space", [L("builder"), L("prune"), L("-f"), L("--max-used-space"), CapSlot()], PruneCeiling);

    /// <summary>A7, the timer, on a builder that predates <c>--max-used-space</c>: <c>--keep-storage</c>.</summary>
    public static readonly CommandTemplate BuilderPruneKeepStorage = Machine("docker-builder-prune-keep-storage", [L("builder"), L("prune"), L("-f"), L("--keep-storage"), CapSlot()], PruneCeiling);

    /// <summary>A7, a button: all of the build cache (plan §5: <c>docker builder prune -af</c>).</summary>
    public static readonly CommandTemplate BuilderPruneAll = Machine("docker-builder-prune-all", [L("builder"), L("prune"), L("-a"), L("-f")], PruneCeiling);

    /// <summary><c>docker system df</c>: Docker's totals, read again after a removal as the cross-check (plan §15c #1).</summary>
    public static readonly CommandTemplate SystemDfRead = CommandTemplate.Fixed(DockerCommands.SystemDf);

    /// <summary>The unattached volumes, read again after A4 so its first sightings are recorded (plan §15b #3).</summary>
    public static readonly CommandTemplate DanglingRead = CommandTemplate.Fixed(DockerCommands.DanglingVolumes);

    /// <summary><c>docker system df -v</c>: every object with its size and labels — read again after A4 for the labels that
    /// decide which unattached volumes are anonymous (their first sightings).</summary>
    public static readonly CommandTemplate InventoryRead = CommandTemplate.Fixed(DockerCommands.SystemDfVerbose);

    /// <summary>Every volume's name, read after A5 to confirm which anonymous volumes went with the containers.</summary>
    public static readonly CommandTemplate VolumeListRead = CommandTemplate.Fixed(DockerCommands.VolumeList);

    /// <summary>The collector's reads every Docker action takes its live look with, as the action declares them.</summary>
    public static IReadOnlyList<CommandTemplate> Reads { get; } =
    [
        CommandTemplate.Fixed(DockerCommands.Version), SystemDfRead, InventoryRead,
        DanglingRead, VolumeListRead, ReadCommandTemplates.ContainerInspect,
    ];

    /// <summary>The size cap as the slot takes it: whole binary gigabytes, <c>20GB</c> (Docker's RAMInBytes reads GB as GiB).</summary>
    public static string Cap(int gigabytes) => gigabytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + "GB";

    private static ArgPart.Slot CapSlot() => new("cap", new SlotKind.Number(0, 100_000, "GB"));

    private static CommandTemplate Machine(string name, IReadOnlyList<ArgPart> parts, TimeSpan ceiling) =>
        new(name, CommandScope.Machine, DockerCommands.Executable, parts, ceiling, OutputCap);

    private static ArgPart.Literal L(string text) => new(text);
}

/// <summary>What a removal said about one object it was asked to remove.</summary>
public enum RemovalVerdict
{
    /// <summary>Docker printed its name: removed.</summary>
    Removed,

    /// <summary>Docker says there is no such object: already gone (plan §15a #0), not a failure, and nothing freed by us.</summary>
    AlreadyGone,

    /// <summary>Docker refused because it is in use (a volume a container attached since the preview, a container that
    /// started since): kept — Docker's own second check doing its job.</summary>
    InUse,

    /// <summary>Docker printed something else about it, or nothing at all: not counted, and the action fails naming it.</summary>
    Unknown,
}

/// <summary>Reading what the Docker write commands print — pure, so every answer is a unit test. The shapes are the docker
/// CLI's own (moby/docker-cli <c>cli/command/volume/remove.go</c>, <c>container/rm.go</c>, <c>image/prune.go</c>,
/// buildx <c>commands/prune.go</c>): a removal prints each removed name on stdout and one <c>Error response from
/// daemon: …</c> line per refused name on stderr; a prune prints what it deleted and a total.</summary>
public static class DockerCleanupAnswers
{
    /// <summary>What a removal of <paramref name="asked"/> said about each name.</summary>
    public static IReadOnlyDictionary<string, (RemovalVerdict Verdict, string Detail)> Removal(IReadOnlyList<string> asked, string stdout, string stderr) =>
        Removal(asked, stdout, stderr, new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>The same, where Docker may name an object by a second name in its refusal (<paramref name="aliases"/>: a
    /// container's id → its name — the daemon's "cannot remove container "/name": container is running" quotes the name).</summary>
    public static IReadOnlyDictionary<string, (RemovalVerdict Verdict, string Detail)> Removal(IReadOnlyList<string> asked, string stdout, string stderr, IReadOnlyDictionary<string, string> aliases)
    {
        var printed = Lines(stdout).ToHashSet(StringComparer.Ordinal);
        var errors = Lines(stderr).ToList();
        return asked.Distinct(StringComparer.Ordinal).ToDictionary(name => name, name => Verdict(name, aliases.GetValueOrDefault(name, string.Empty), printed, errors), StringComparer.Ordinal);
    }

    /// <summary>Docker's own "Total reclaimed space: …" (image prune) or "Total: …" (builder prune), in bytes; unavailable,
    /// never 0, when it printed neither.</summary>
    public static Reading<long> Reclaimed(string stdout)
    {
        var total = Lines(stdout).Select(l => l.Replace('\t', ' ')).LastOrDefault(l => l.StartsWith("Total reclaimed space:", StringComparison.Ordinal) || l.StartsWith("Total:", StringComparison.Ordinal));
        return total is null
            ? Reading.Missing<long>("Docker printed no total of what it reclaimed")
            : DockerText.Bytes(total[(total.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim());
    }

    /// <summary>The images an image prune deleted (<c>deleted: sha256:…</c>) and untagged (<c>untagged: …</c>).</summary>
    public static IReadOnlyList<ActionItem> PrunedImages(string stdout) =>
        [.. Lines(stdout).Where(l => l.StartsWith("deleted:", StringComparison.Ordinal) || l.StartsWith("untagged:", StringComparison.Ordinal))
            .Select(l => new ActionItem(l.StartsWith('d') ? "image" : "tag", l[(l.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim(), null))];

    /// <summary>The cache entries a builder prune listed (its table rows: an id, then the columns).</summary>
    public static IReadOnlyList<ActionItem> PrunedCache(string stdout) =>
        [.. Lines(stdout).Select(l => l.Replace('\t', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(f => f.Length >= 2 && f[0] != "ID" && !f[0].StartsWith("Total", StringComparison.Ordinal) && f[0].TrimEnd('*').All(char.IsAsciiLetterOrDigit))
            .Select(f => new ActionItem("build cache", f[0].TrimEnd('*'), null))];

    /// <summary>Which size cap a builder prune's help offers: <c>--max-used-space</c> (buildx, Docker 28+) first, then
    /// <c>--keep-storage</c>; empty when it lists neither.</summary>
    public static string CapFlag(string help) =>
        help.Contains("--max-used-space", StringComparison.Ordinal) ? "--max-used-space"
        : help.Contains("--keep-storage", StringComparison.Ordinal) ? "--keep-storage"
        : string.Empty;

    /// <summary>Docker's own figure for one type in <c>docker system df</c> (the cross-check beside a measured freed figure).</summary>
    public static Reading<long> TypeSize(Reading<IReadOnlyList<DockerTotal>> totals, string type) =>
        totals.Bind(list => list.FirstOrDefault(t => t.Type == type) is { } row ? row.SizeBytes : Reading.Missing<long>($"docker system df has no {type} line"));

    private static readonly string[] GonePhrases = ["no such volume", "no such container"];
    private static readonly string[] InUsePhrases = ["volume is in use", "container is running", "running container", "is restarting", "removal of container", "is already in progress"];

    private static (RemovalVerdict, string) Verdict(string name, string alias, IReadOnlySet<string> printed, IReadOnlyList<string> errors)
    {
        if (printed.Contains(name))
        {
            return (RemovalVerdict.Removed, string.Empty);
        }

        var line = ErrorAbout(name, alias, errors);
        return line switch
        {
            "" => (RemovalVerdict.Unknown, "Docker neither printed its name nor said why"),
            _ when Says(line, GonePhrases) => (RemovalVerdict.AlreadyGone, DockerCli.Quote(line)),
            _ when Says(line, InUsePhrases) => (RemovalVerdict.InUse, DockerCli.Quote(line)),
            _ => (RemovalVerdict.Unknown, DockerCli.Quote(line)),
        };
    }

    /// <summary>The first error line about <paramref name="name"/>; empty when Docker said nothing about it.</summary>
    private static string ErrorAbout(string name, string alias, IReadOnlyList<string> errors) => errors.FirstOrDefault(e => Names(e, name, alias)) ?? string.Empty;

    /// <summary>Whether an error line is about <paramref name="name"/> — or quotes its <paramref name="alias"/> (a container's
    /// name, as the daemon writes it: <c>"/name"</c>).</summary>
    private static bool Names(string line, string name, string alias) =>
        line.Contains(name, StringComparison.Ordinal) || (alias.Length > 0 && line.Contains($"\"/{alias}\"", StringComparison.Ordinal));

    private static bool Says(string line, IEnumerable<string> phrases) => phrases.Any(p => line.Contains(p, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> Lines(string text) => text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
}
