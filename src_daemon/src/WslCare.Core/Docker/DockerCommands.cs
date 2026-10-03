using System.Globalization;

using WslCare.Core.Processes;

namespace WslCare.Core.Docker;

/// <summary>
/// Every <c>docker</c> command the daemon runs, in ONE place (plan §4.3): all of them READ. The argv
/// of each is built here and nowhere else, so the scenario fakes, the live contract and the collectors
/// agree on it by construction — a fixture is replayed for exactly the argv the product sends.
/// </summary>
/// <remarks>
/// <para>Every command carries its own ceiling. <c>docker system df -v</c> "takes seconds to minutes"
/// (plan §4.3) — measured 2 s here on 2026-10-02 with 48 volumes and 72 cache entries, on a Docker that had
/// held 474 volumes and 2 190 cache entries the same morning — so it gets two minutes; a probe that only
/// asks the engine its version gets ten seconds, because a daemon that is still starting must read as
/// <em>unavailable</em>, not hang the panel.</para>
/// <para>The output caps are generous on purpose: the verbose disk usage of the morning's Docker would
/// have been several megabytes of JSON, and an answer cut by the cap is reported unparseable rather than
/// read as a shorter truth.</para>
/// </remarks>
public static class DockerCommands
{
    public const string Executable = "docker";

    /// <summary>How many container ids one <c>container inspect</c> takes: 100 full ids are 6.5 k
    /// characters, far below the 32 k a Windows command line holds.</summary>
    public const int InspectBatch = 100;

    /// <summary>
    /// The fields of <c>docker container inspect</c> the daemon reads, and ONLY those: the template is
    /// evaluated inside the docker CLI, so a container's environment, command and bind-mount sources —
    /// where secrets and company paths live — never reach this process or a fixture. One JSON object per
    /// container, per line.
    /// </summary>
    /// <remarks>A mount is read with <c>index $m "Name"</c>, never <c>$m.Name</c>: the CLI evaluates the template
    /// over the decoded JSON, and a bind mount has no <c>Name</c> key, so <c>$m.Name</c> fails the WHOLE command
    /// ("map has no entry for key Name") — found by the live contract on 2026-10-02, on this machine's 29
    /// containers; <c>index</c> answers null for a missing key.</remarks>
    public const string InspectTemplate =
        "{\"id\":{{json .Id}},\"name\":{{json .Name}},\"created\":{{json .Created}},\"state\":{{json .State.Status}}," +
        "\"startedAt\":{{json .State.StartedAt}},\"finishedAt\":{{json .State.FinishedAt}},\"image\":{{json .Image}}," +
        "\"logDriver\":{{json .HostConfig.LogConfig.Type}},\"logMaxSize\":{{json (index .HostConfig.LogConfig.Config \"max-size\")}}," +
        "\"logPath\":{{json .LogPath}},\"testcontainers\":{{json (index .Config.Labels \"org.testcontainers\")}}," +
        "\"keep\":{{json (index .Config.Labels \"wsl-care.keep\")}}," +
        "\"mounts\":[{{range $i, $m := .Mounts}}{{if $i}},{{end}}{\"type\":{{json (index $m \"Type\")}},\"name\":{{json (index $m \"Name\")}}}{{end}}]}";

    private const int SmallCap = 1024 * 1024;
    private const int LargeCap = 64 * 1024 * 1024;

    /// <summary>The engine's version: is a daemon there at all, and which (plan §5 A4 refuses below 23).</summary>
    public static readonly TimeSpan ProbeCeiling = TimeSpan.FromSeconds(10);

    /// <summary>A listing: volumes, containers, stats, events.</summary>
    public static readonly TimeSpan ListingCeiling = TimeSpan.FromSeconds(30);

    /// <summary><c>docker system df</c> walks every layer and volume (plan §4.3: seconds to minutes).</summary>
    public static readonly TimeSpan DiskUsageCeiling = TimeSpan.FromMinutes(2);

    public static ToolCommand Version { get; } = new(Executable, "version", ["version", "--format", "{{json .}}"], ProbeCeiling, SmallCap);

    /// <summary>The totals per type — count, active, size, reclaimable — Docker's own figures (the
    /// "Docker after" line of the 2026-10-02 record).</summary>
    public static ToolCommand SystemDf { get; } = new(Executable, "system-df", ["system", "df", "--format", "{{json .}}"], DiskUsageCeiling, SmallCap);

    /// <summary>Every image, container, volume and build-cache entry with its size.</summary>
    public static ToolCommand SystemDfVerbose { get; } = new(Executable, "system-df-v", ["system", "df", "-v", "--format", "{{json .}}"], DiskUsageCeiling, LargeCap);

    /// <summary>The volumes no container refers to — A4's re-checked list (plan §15 #4).</summary>
    public static ToolCommand DanglingVolumes { get; } = new(Executable, "volume-ls-dangling", ["volume", "ls", "--filter", "dangling=true", "--format", "{{.Name}}"], ListingCeiling, LargeCap);

    /// <summary>The name of EVERY volume, attached or not — what A5 reads after <c>docker rm -v</c> to confirm which anonymous
    /// volumes went with the containers (E3.S2: a volume it cannot confirm gone counts nothing).</summary>
    public static ToolCommand VolumeList { get; } = new(Executable, "volume-ls", ["volume", "ls", "--format", "{{.Name}}"], ListingCeiling, LargeCap);

    /// <summary>Every container, one JSON line each. Not used by <c>preview</c> (the verbose disk usage
    /// lists containers with their sizes); the live contract holds its rows to the same parser and the
    /// same ids, and the events follower (E2.S3) can name containers with it.</summary>
    public static ToolCommand ContainerList { get; } = new(Executable, "ps-a", ["ps", "-a", "--no-trunc", "--format", "{{json .}}"], ListingCeiling, LargeCap);

    /// <summary><c>docker stats --no-stream</c>: the per-container memory a FULL run samples (plan §4.2,
    /// §15b #5) — never <c>status</c>.</summary>
    public static ToolCommand Stats { get; } = new(Executable, "stats", ["stats", "--no-stream", "--format", "{{json .}}"], ListingCeiling, LargeCap);

    /// <summary>The engine instance and its start (<see cref="EngineMark"/>): the default <c>bridge</c> network's id and creation
    /// instant, which the engine re-creates at every start. The follower records it at each coverage marker so a backfill can
    /// tell an idle engine (nothing happened) from a restarted one (its buffer was lost).</summary>
    public static ToolCommand EngineStart { get; } = new(Executable, "network-inspect-bridge", ["network", "inspect", "bridge", "--format", "{\"id\":{{json .Id}},\"created\":{{json .Created}}}"], ProbeCeiling, SmallCap);

    /// <summary>
    /// The leading words of every command built here. Each one only READS; a test holds every command
    /// to this list, and the scenario holds every argv the fakes saw to it — so a write verb cannot slip
    /// into a collector without one of the two going red.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> ReadVerbs { get; } =
    [
        ["version"], ["system", "df"], ["volume", "ls"], ["ps"], ["container", "inspect"], ["stats"], ["events"], ["network", "inspect"],
    ];

    /// <summary>The fields of <see cref="InspectTemplate"/> for 1 to <see cref="InspectBatch"/> containers.</summary>
    public static ToolCommand ContainerInspect(IReadOnlyList<string> ids)
    {
        if (ids.Count is 0 or > InspectBatch)
        {
            throw new ArgumentOutOfRangeException(nameof(ids), ids.Count, $"container inspect takes 1 to {InspectBatch} ids per call");
        }

        return new(Executable, "container-inspect", ["container", "inspect", "--format", InspectTemplate, .. ids], ListingCeiling, LargeCap)
        {
            Summary = $"docker container inspect (the wsl-care template, {ids.Count} containers)",
        };
    }

    /// <summary>Container starts in a bounded window, one JSON line each (the live contract's window of the last 24 h).</summary>
    public static ToolCommand Events(DateTimeOffset since, DateTimeOffset until) =>
        new(
            Executable,
            "events",
            ["events", "--since", Rfc3339(since), "--until", Rfc3339(until), "--filter", "type=container", "--filter", "event=start", "--format", "{{json .}}"],
            ListingCeiling,
            LargeCap);

    /// <summary>
    /// The follower's backfill (plan §15b #0): EVERY event Docker still buffers in a past window, unfiltered, one JSON
    /// line each. Unfiltered on purpose — the oldest event of any kind is the proof of how far back the in-memory
    /// buffer reaches (measured 2026-10-02: 255 healthcheck <c>exec_*</c> events and no start), and so whether a gap
    /// since the last marker can be filled at all. The buffer is a few hundred events, so the answer stays small.
    /// </summary>
    public static ToolCommand Backfill(DateTimeOffset since, DateTimeOffset until) =>
        new(Executable, "events-backfill", ["events", "--since", Rfc3339(since), "--until", Rfc3339(until), "--format", "{{json .}}"], ListingCeiling, LargeCap);

    /// <summary>
    /// One segment of the live stream: container starts from <paramref name="since"/> (replayed from Docker's buffer)
    /// until <paramref name="until"/>, a FUTURE instant, at which Docker closes the stream by itself. A segment is
    /// bounded so the stream is a wait with a ceiling like any other (reliability rule) — its ceiling is the segment
    /// plus <paramref name="slack"/>, and each segment's end is a coverage marker.
    /// </summary>
    public static ToolCommand EventStream(DateTimeOffset since, DateTimeOffset until, TimeSpan slack) =>
        new(
            Executable,
            "events-stream",
            ["events", "--since", Rfc3339(since), "--until", Rfc3339(until), "--filter", "type=container", "--filter", "event=start", "--format", "{{json .}}"],
            until - since + slack,
            LargeCap);

    /// <summary>Whether an argv (without <c>docker</c>) starts with one of <see cref="ReadVerbs"/>.</summary>
    public static bool IsReadVerb(IReadOnlyList<string> arguments) =>
        ReadVerbs.Any(verb => verb.Count <= arguments.Count && verb.Zip(arguments).All(p => string.Equals(p.First, p.Second, StringComparison.Ordinal)));

    private static string Rfc3339(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
