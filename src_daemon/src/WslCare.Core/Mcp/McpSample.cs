using WslCare.Core.Collectors;
using WslCare.Core.Config;

namespace WslCare.Core.Mcp;

/// <summary>What an MCP server instance is doing (plan §15q E7.S2d, Decided 6) — a closed set, decided in this order.</summary>
public enum McpKind
{
    /// <summary>Its CPU could not be measured, over the interval or across the window.</summary>
    Unknown,

    /// <summary>Below the idle CPU line and younger than the idle minimum age.</summary>
    Starting,

    /// <summary>Below the idle CPU line and at least the idle minimum age.</summary>
    Idle,

    /// <summary>At or above the idle CPU line, with a recent log write or none derivable.</summary>
    Busy,

    /// <summary>At or above the idle CPU line while its newest log write is KNOWN and older than the activity window — the state
    /// measured 2026-10-06 (27–54 % of a core each, no log line for 10+ minutes).</summary>
    BusyWithoutActivity,
}

/// <summary>Whose an instance is: the AI-agent session it runs under, or none because its agent is gone.</summary>
public abstract record McpOwner
{
    private McpOwner()
    {
    }

    /// <summary>The first ancestor that is a catalogue agent: its pid, the agent's name, its shown (redacted, cut) command line.</summary>
    public sealed record Agent(int Pid, string Name, string CommandLine) : McpOwner;

    /// <summary>Re-parented to init (or the user's systemd manager) with no agent above it: an MCP server whose agent died — kept,
    /// because that is a leak this metric exists to show (plan §15q E7.S2d, Decided 1).</summary>
    public sealed record Orphaned : McpOwner;
}

/// <summary>What an instance's CPU figure was measured over (plan E14 S1) — a closed set; its JSON name is given at the edge.</summary>
public enum McpCpuBasis
{
    /// <summary>Not measured: <see cref="McpInstance.CpuPercent"/> says why.</summary>
    None,

    /// <summary>Two reads across <c>mcpServers.cpuWindowMilliseconds</c> — the fallback for an instance with no baseline.</summary>
    Window,

    /// <summary>The interval since the ledger's point of this identity (<c>mcpServers.cpuIntervalMinSeconds</c> to
    /// <c>…MaxMinutes</c>) — what sees a burst the window falls between.</summary>
    Interval,
}

/// <summary>One instance's CPU: the figure (% of one core), what it was measured over, and how long that was.</summary>
public sealed record McpCpu(Reading<double> Percent, McpCpuBasis Basis, TimeSpan Over)
{
    public static McpCpu Unmeasured(string reason) => new(Reading.Missing<double>(reason), McpCpuBasis.None, TimeSpan.Zero);
}

/// <summary>One instance as measured: the process, its server, its owner, its CPU (across the window or the interval since its
/// previous sample), its newest log write and its kind.</summary>
public sealed record McpInstance(ProcessEntry Process, string Server, McpOwner Owner, Reading<double> CpuPercent, Reading<DateTimeOffset> LastLogWrite, McpKind Kind)
{
    /// <summary>What <see cref="CpuPercent"/> was measured over (plan E14 S1).</summary>
    public McpCpuBasis CpuBasis { get; init; } = McpCpuBasis.Window;

    /// <summary>How long <see cref="CpuPercent"/> was measured over; zero when it was not measured.</summary>
    public TimeSpan CpuOver { get; init; }
}

/// <summary>Whether this sample's readings were recorded in the CPU ledger for the next one (plan E14 S1).</summary>
/// <param name="File">The ledger this caller reads and writes; empty when it has none.</param>
/// <param name="Recorded">Written by this sample, or already holding what it would write.</param>
/// <param name="Reason">Why not recorded; empty when recorded.</param>
public sealed record McpCpuBaseline(string File, bool Recorded, string Reason)
{
    public static McpCpuBaseline NotRecorded(string file, string reason) => new(file, false, reason);
}

/// <summary>How a server's starts were counted — a closed set (coai code round finding 0); its JSON name is given at the edge.</summary>
public enum McpStartsBasis
{
    /// <summary>From the names of its log files (one per run): every start in the window.</summary>
    LogNames,

    /// <summary>No log layout: the live instances younger than the window — a LOWER bound (a process killed within the window is
    /// not seen). Distinct pids across runs are not tracked: the only state status keeps is the CPU ledger of the instances it
    /// sees (plan E14 S1), and the timer runs every few hours.</summary>
    LiveYounger,
}

/// <summary>One start inside the window (the owner's correction of 2026-10-07: churn must be attributable to a time — the storm
/// of 2026-10-06 began before the binary that was blamed for it was installed).</summary>
/// <param name="At">When it started: its run log's name (UTC), or for the live-younger fallback the process's start.</param>
/// <param name="LastWrite">Its run log's last write — with <paramref name="At"/>, how long that run lived when it is gone.</param>
/// <param name="Running">Its pid runs now as this server.</param>
public sealed record McpStart(DateTimeOffset At, int Pid, DateTimeOffset LastWrite, bool Running);

/// <summary>A server's starts in the window, and how they were counted; <paramref name="Recent"/> lists them, newest first, at most
/// <c>mcpServers.maxStartsListed</c> (the count is never capped).</summary>
public sealed record McpStarts(Reading<int> Count, McpStartsBasis Basis, int WindowMinutes)
{
    public IReadOnlyList<McpStart> Recent { get; init; } = [];
}

/// <summary>One watched server: its instances and its starts.</summary>
public sealed record McpServerSummary(string Name, int Count, McpStarts Starts);

/// <summary>The MCP servers of the AI agents in one sample (plan §15q E7.S2d).</summary>
/// <param name="WindowMilliseconds">The CPU window in force.</param>
/// <param name="Count">Every instance found — <see cref="Instances"/> holds at most <c>mcpServers.maxInstances</c> of them.</param>
/// <param name="NotUnderAgent">Processes of a watched server under a live process that is no agent: not instances, only counted.</param>
/// <param name="HeldBytes">Σ <c>RssAnon + RssShmem</c> over every instance.</param>
public sealed record McpSample(int WindowMilliseconds, int Count, int NotUnderAgent, long HeldBytes, IReadOnlyList<McpInstance> Instances, IReadOnlyList<McpServerSummary> Servers)
{
    /// <summary>Whether this sample's readings were recorded for the next one's interval (plan E14 S1).</summary>
    public McpCpuBaseline Baseline { get; init; } = McpCpuBaseline.NotRecorded(string.Empty, "no MCP server instance to record");

    public int IdleCount => Instances.Count(i => i.Kind == McpKind.Idle);

    public int BusyWithoutActivityCount => Instances.Count(i => i.Kind == McpKind.BusyWithoutActivity);

    /// <summary>The instances whose CPU was measured.</summary>
    public int CpuMeasured => Instances.Count(i => i.CpuPercent.IsAvailable);

    /// <summary>Σ CPU % ÷ 100 over the measured instances; unavailable when instances exist and none could be measured.</summary>
    public Reading<double> CpuCores => Count > 0 && CpuMeasured == 0
        ? Reading.Missing<double>("no instance's CPU could be measured, over its interval or across the window")
        : Reading.Of(Math.Round(Instances.Sum(i => i.CpuPercent.ValueOr(0)) / PercentPerCore, 2));

    /// <summary>One core, in percent — the unit of every CPU figure here.</summary>
    public const double PercentPerCore = 100;
}

/// <summary>The settings one sample runs under — every number a key (the owner's rule of 2026-10-05).</summary>
public sealed record McpSettings(
    IReadOnlyList<McpServerEntry> Watched,
    TimeSpan Window,
    double IdleCpuPercent,
    TimeSpan IdleMinAge,
    TimeSpan ActivityWindow,
    TimeSpan StartsWindow,
    int MaxInstances,
    int MaxLogEntries,
    TimeSpan LogListBudget,
    int MaxStartsListed)
{
    /// <summary>The interval bounds a ledger point must lie within to be a baseline (plan E14 S1).</summary>
    public required McpCpuBounds Bounds { get; init; }

    /// <summary>The most bytes of a ledger read back (<c>records.maxStateFileBytes</c>).</summary>
    public required int LedgerMaxBytes { get; init; }

    public static McpSettings From(EffectiveConfig config) => new(
        [.. McpServerCatalogue.Servers.Where(s => config.TextList(ConfigKeys.McpServers.Watched).Contains(s.Name, StringComparer.Ordinal))],
        TimeSpan.FromMilliseconds(config.Int(ConfigKeys.McpServers.CpuWindowMilliseconds)),
        config.Int(ConfigKeys.McpServers.IdleCpuPercent),
        TimeSpan.FromMinutes(config.Int(ConfigKeys.McpServers.IdleMinAgeMinutes)),
        TimeSpan.FromMinutes(config.Int(ConfigKeys.McpServers.ActivityWindowMinutes)),
        TimeSpan.FromMinutes(config.Int(ConfigKeys.McpServers.StartsWindowMinutes)),
        config.Int(ConfigKeys.McpServers.MaxInstances),
        config.Int(ConfigKeys.McpServers.MaxLogEntries),
        TimeSpan.FromMilliseconds(config.Int(ConfigKeys.McpServers.LogListMilliseconds)),
        config.Int(ConfigKeys.McpServers.MaxStartsListed))
    {
        Bounds = new(TimeSpan.FromSeconds(config.Int(ConfigKeys.McpServers.CpuIntervalMinSeconds)), TimeSpan.FromMinutes(config.Int(ConfigKeys.McpServers.CpuIntervalMaxMinutes))),
        LedgerMaxBytes = config.Int(ConfigKeys.Records.MaxStateFileBytes),
    };
}
