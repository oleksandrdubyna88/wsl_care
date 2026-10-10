using WslCare.Core.Config;
using System.Text.Json;

using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;
using WslCare.Core.Json;

namespace WslCare.Core.Actions.Suspects;

/// <summary>One AI-agent process as the history keeps it: its identity (pid AND start ticks, within <see cref="AgentCpuFile.BootId"/>),
/// the CPU ticks last seen, since when they have not changed and when it was last seen — on the wall clock AND on the
/// system's monotonic clock (milliseconds since boot, E7.S2b review A-M4) — and the longest gap between two sightings since
/// the ticks last moved.</summary>
/// <remarks>The monotonic fields are 0 in a history written before the review: such an entry is "no history" (never idle).</remarks>
public sealed record AgentCpuEntry(int Pid, long StartTicks, long CpuTicks, DateTimeOffset UnchangedSince, DateTimeOffset Seen, long UnchangedSinceMs = 0, long SeenMs = 0, long LongestGapMs = 0);

/// <summary><c>/var/lib/wsl-care/agent-cpu.json</c> (plan §15q E7.S2b): one boot's entries.</summary>
public sealed record AgentCpuFile(int SchemaVersion, string BootId, IReadOnlyList<AgentCpuEntry> Entries)
{
    public static readonly AgentCpuFile Empty = new(Core.SchemaVersion.Current, string.Empty, []);
}

/// <summary>
/// "No CPU for N hours", MEASURED (plan §15q E7.S2b item 3): every root run records, for each AI-agent process of a non-root
/// account — and, since E14, each watched MCP server (A19) and each .NET build server (A3's timer) — its cumulative CPU ticks
/// by identity — <c>(pid, boot_id, start ticks)</c>, <c>/proc/&lt;pid&gt;/stat</c> fields 22
/// and 14 + 15. A process has been idle since the OLDEST sample of the same identity whose ticks equal now's; a different boot,
/// a different start (a reused pid) or no history at all is "no history" — never idle, so the first runs end nothing.
/// </summary>
/// <remarks>
/// <para><b>Both clocks, a dense chain</b> (review A-M4): a wall clock that jumps forward after the host slept would pass the
/// window at once, so the idle time is the SHORTER of the wall clock's and the monotonic clock's, and it counts only when no two
/// sightings since the ticks last moved — and not the newest sighting and now — lie more than two timer periods apart.</para>
/// <para>The file is root's state (read back with <see cref="IFileSystem.ReadStateFile"/>: uid 0, no link, no wait, a cap),
/// written atomically and PRIVATE (0600, review A-L1: it names another account's processes), holding live processes only, at
/// most <see cref="MaxEntries"/> — past it the OLDEST processes are dropped, which only makes them "no history" (kept).</para>
/// </remarks>
public static class AgentCpuHistory
{
    public const string FileName = "agent-cpu.json";

    public static int MaxEntries => Tuning.Current.Int(ConfigKeys.AgentCpu.MaxEntries);

    /// <summary>A full history (512 identities, ~310 B each with both clocks) is ~157 KiB; the cap leaves room above it.</summary>
    public static int MaxBytes => Tuning.Current.Int(ConfigKeys.AgentCpu.MaxBytes);

    /// <summary>The longest gap a dense chain allows between two sightings: two timer periods (one late or missed run).</summary>
    public static TimeSpan MaxGap => Tuning.Current.Hours(ConfigKeys.Timer.PeriodHours) * 2;

    public static string File(IHostPaths paths) => paths.Rules.Join(paths.StateDirectory, FileName);

    /// <summary>The recorded history; <see cref="AgentCpuFile.Empty"/> when there is none or it cannot be read (= no history).</summary>
    public static AgentCpuFile Read(IHostPaths paths, IFileSystem files) =>
        files.ReadStateFile(File(paths), MaxBytes) is FileReadResult.Content content ? Parse(content.Bytes) : AgentCpuFile.Empty;

    private static AgentCpuFile Parse(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize(bytes, WslCareJsonContext.Default.AgentCpuFile) is { BootId.Length: > 0 } file ? file : AgentCpuFile.Empty;
        }
        catch (JsonException)
        {
            return AgentCpuFile.Empty;
        }
    }

    /// <summary>The next history: <paramref name="samples"/> (the live recorded processes now) merged into <paramref name="before"/> —
    /// an identity whose ticks did not move keeps its "unchanged since" and adds the gap since its last sighting, one that moved
    /// starts again now, a new one starts now; identities not alive now are dropped; another boot starts afresh.</summary>
    public static AgentCpuFile Next(AgentCpuFile before, string bootId, IReadOnlyList<PidSample> samples, SampleTime at)
    {
        var known = before.BootId == bootId
            ? before.Entries.ToDictionary(e => (e.Pid, e.StartTicks))
            : [];
        var entries = samples
            .Select(s => known.TryGetValue((s.Pid, s.StartTicks), out var was) && was.CpuTicks == s.CpuTicks && was.SeenMs > 0
                ? was with { Seen = at.Wall, SeenMs = at.MonotonicMs, LongestGapMs = Math.Max(was.LongestGapMs, at.MonotonicMs - was.SeenMs) }
                : new AgentCpuEntry(s.Pid, s.StartTicks, s.CpuTicks, at.Wall, at.Wall, at.MonotonicMs, at.MonotonicMs))
            .OrderByDescending(e => e.StartTicks)
            .Take(MaxEntries)
            .OrderBy(e => e.Pid)
            .ToList();
        return new AgentCpuFile(Core.SchemaVersion.Current, bootId, entries);
    }

    /// <summary>How long <paramref name="sample"/> has used no CPU, by the history — the shorter of the two clocks, and only over a
    /// dense chain of sightings; <see cref="TimeSpan.Zero"/> when the history does not hold this identity with these ticks in this
    /// boot, when it has no monotonic reading, or when a gap breaks the chain (no history = not idle).</summary>
    public static TimeSpan IdleFor(AgentCpuFile history, string bootId, PidSample sample, SampleTime at) =>
        history.BootId.Length > 0 && history.BootId == bootId
        && history.Entries.FirstOrDefault(e => e.Pid == sample.Pid && e.StartTicks == sample.StartTicks) is { } entry
        && entry.CpuTicks == sample.CpuTicks && Dense(entry, at)
            ? Shorter(at.Wall - entry.UnchangedSince, TimeSpan.FromMilliseconds(at.MonotonicMs - entry.UnchangedSinceMs))
            : TimeSpan.Zero;

    /// <summary>How long each of <paramref name="pids"/> has used no CPU NOW — each read from /proc, merged with the recorded
    /// history in memory only (a preview writes no state) — one value per pid that is readable and not root's; EMPTY when the
    /// boot id cannot be read, so no process's idle time can be told (E14 S3; coai code round 2026-10-08, finding 5: the one
    /// query a caller makes instead of sampling, merging and judging by itself).</summary>
    public static IReadOnlyList<TimeSpan> IdleNow(LinuxHostPaths paths, IFileSystem files, IReadOnlyList<int> pids, SampleTime at)
    {
        var boot = BootIdentity.Read(paths, files);
        if (boot.Length == 0)
        {
            return [];
        }

        var samples = pids.Distinct().Select(pid => SuspectTermination.Sample(files, paths, pid)).OfType<PidSample>().Where(s => s.Uid != 0).ToList();
        var history = Next(Read(paths, files), boot, samples, at);
        return [.. samples.Select(s => IdleFor(history, boot, s, at))];
    }

    private static bool Dense(AgentCpuEntry entry, SampleTime at)
    {
        var maxGapMs = (long)MaxGap.TotalMilliseconds;
        return entry.SeenMs > 0 && entry.LongestGapMs <= maxGapMs && at.MonotonicMs - entry.SeenMs <= maxGapMs && at.MonotonicMs >= entry.UnchangedSinceMs;
    }

    private static TimeSpan Shorter(TimeSpan wall, TimeSpan monotonic) => wall < monotonic ? wall : monotonic;

    /// <summary>Empty when written; otherwise why not.</summary>
    public static string Write(IHostPaths paths, IFileSystem files, AgentCpuFile history)
    {
        try
        {
            files.CreateDirectory(paths.StateDirectory);
            var json = JsonSerializer.SerializeToUtf8Bytes(history, WslCareJsonContext.Default.AgentCpuFile);
            return files.WritePrivateFileAtomically(File(paths), json, new DeletionScope(paths.StateDirectory, "agent-cpu")) is DeletionVerdict.Refused refused ? refused.Reason : string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    /// <summary>The AI-agent processes AND the processes of the WATCHED MCP servers (plan E14 S2a: A19's evidence — one history,
    /// one sampler; coai code round 2026-10-08: the watched list, never the whole catalogue, so a server A19 may judge is one the
    /// history records) of non-root accounts in <paramref name="processes"/>, sampled from <c>/proc</c> now, each pid once (the merge
    /// keys by identity and would refuse a duplicate — the risk consultation of 2026-10-08) — and, since E14 S3, the .NET build
    /// servers (A3's timer runs its shutdown only when every one is measured idle).</summary>
    public static IReadOnlyList<PidSample> Sample(LinuxHostPaths paths, IFileSystem files, IEnumerable<ProcessEntry> processes, IReadOnlyList<Mcp.McpServerEntry> watched) =>
        Sample(paths, files, processes, watched, []);

    /// <summary>The same, and — since E14 S7b.2 — the interop relays of <paramref name="relays"/> (A21's idle floor): a relay's
    /// argv[0] is <c>init</c>, so the watched list alone never records one.</summary>
    public static IReadOnlyList<PidSample> Sample(LinuxHostPaths paths, IFileSystem files, IEnumerable<ProcessEntry> processes, IReadOnlyList<Mcp.McpServerEntry> watched, IReadOnlyList<Mcp.McpServerEntry> relays) =>
        [.. processes
            .Where(p => p.Pid > 1 && p.User != "root" && (IsRecorded(p, watched) || InteropRelays.ServerOf(p, relays) is not null))
            .Select(p => p.Pid)
            .Distinct()
            .Select(pid => SuspectTermination.Sample(files, paths, pid))
            .OfType<PidSample>()
            .Where(s => s.Uid != 0)];

    /// <summary>An AI agent's process (A18), a watched MCP server's (A19) or a .NET build server (A3's timer, E14 S3).</summary>
    private static bool IsRecorded(ProcessEntry process, IReadOnlyList<Mcp.McpServerEntry> watched) =>
        process.Family is ProcessFamilies.AiAgents or ProcessFamilies.DotnetBuildServers || Mcp.McpInstances.ServerOf(process, watched) is not null;

    /// <summary>The same over every catalogued MCP server (A18's judgement, which reads the agents only).</summary>
    public static IReadOnlyList<PidSample> Sample(LinuxHostPaths paths, IFileSystem files, IEnumerable<ProcessEntry> processes) =>
        Sample(paths, files, processes, Mcp.McpServerCatalogue.Servers);

    /// <summary>One root run's record as THIS configuration has it (the timer's full run and the watch, plan E14 S7b.2): the watched
    /// MCP servers and the catalogued servers' interop relays. Every writer records the same set — an identity a writer leaves out
    /// is dropped from the history, which would reset its idle time — so this is the ONLY writer (coai code round 2026-10-10,
    /// finding 1: the overloads that took a narrower set are gone). Empty when written.</summary>
    public static string Record(LinuxHostPaths paths, IFileSystem files, IEnumerable<ProcessEntry> processes, SampleTime at, EffectiveConfig config) =>
        Record(paths, files, processes, at, Mcp.McpSettings.From(config).Watched, InteropRelays.Servers(config));

    private static string Record(LinuxHostPaths paths, IFileSystem files, IEnumerable<ProcessEntry> processes, SampleTime at, IReadOnlyList<Mcp.McpServerEntry> watched, IReadOnlyList<Mcp.McpServerEntry> relays)
    {
        var boot = BootIdentity.Read(paths, files);
        return boot.Length == 0
            ? "the boot id cannot be read; the AI-agent CPU history is not recorded"
            : Write(paths, files, Next(Read(paths, files), boot, Sample(paths, files, processes, watched, relays), at));
    }
}
