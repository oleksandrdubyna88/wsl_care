using WslCare.Core.Collectors;
using WslCare.Core.Config;

namespace WslCare.Core.Mcp;

/// <summary>What an MCP server instance is doing (plan §15q E7.S2d, Decided 6) — a closed set, decided in this order.</summary>
public enum McpKind
{
    /// <summary>Its CPU could not be measured across the window.</summary>
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

/// <summary>One instance as measured: the process, its server, its owner, its CPU across the window, its newest log write and
/// its kind.</summary>
public sealed record McpInstance(ProcessEntry Process, string Server, McpOwner Owner, Reading<double> CpuPercent, Reading<DateTimeOffset> LastLogWrite, McpKind Kind);

/// <summary>How a server's starts were counted.</summary>
public static class McpStartsBasis
{
    /// <summary>From the names of its log files (one per run): every start in the window.</summary>
    public const string LogNames = "logNames";

    /// <summary>No log layout: the live instances younger than the window — a LOWER bound (a process killed within the window is
    /// not seen). Distinct pids across runs are not tracked: status writes no state and the timer runs every few hours.</summary>
    public const string LiveYounger = "liveYounger";
}

/// <summary>A server's starts in the window, and how they were counted.</summary>
public sealed record McpStarts(Reading<int> Count, string Basis, int WindowMinutes);

/// <summary>One watched server: its instances and its starts.</summary>
public sealed record McpServerSummary(string Name, int Count, McpStarts Starts);

/// <summary>The MCP servers of the AI agents in one sample (plan §15q E7.S2d).</summary>
/// <param name="WindowMilliseconds">The CPU window in force.</param>
/// <param name="Count">Every instance found — <see cref="Instances"/> holds at most <c>mcpServers.maxInstances</c> of them.</param>
/// <param name="NotUnderAgent">Processes of a watched server under a live process that is no agent: not instances, only counted.</param>
/// <param name="HeldBytes">Σ <c>RssAnon + RssShmem</c> over every instance.</param>
public sealed record McpSample(int WindowMilliseconds, int Count, int NotUnderAgent, long HeldBytes, IReadOnlyList<McpInstance> Instances, IReadOnlyList<McpServerSummary> Servers)
{
    public int IdleCount => Instances.Count(i => i.Kind == McpKind.Idle);

    public int BusyWithoutActivityCount => Instances.Count(i => i.Kind == McpKind.BusyWithoutActivity);

    /// <summary>The instances whose CPU was measured.</summary>
    public int CpuMeasured => Instances.Count(i => i.CpuPercent.IsAvailable);

    /// <summary>Σ CPU % ÷ 100 over the measured instances; unavailable when instances exist and none could be measured.</summary>
    public Reading<double> CpuCores => Count > 0 && CpuMeasured == 0
        ? Reading.Missing<double>("no instance's CPU could be measured across the window")
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
    TimeSpan LogListBudget)
{
    public static McpSettings From(EffectiveConfig config) => new(
        [.. McpServerCatalogue.Servers.Where(s => config.TextList(ConfigKeys.McpServers.Watched).Contains(s.Name, StringComparer.Ordinal))],
        TimeSpan.FromMilliseconds(config.Int(ConfigKeys.McpServers.CpuWindowMilliseconds)),
        config.Int(ConfigKeys.McpServers.IdleCpuPercent),
        TimeSpan.FromMinutes(config.Int(ConfigKeys.McpServers.IdleMinAgeMinutes)),
        TimeSpan.FromMinutes(config.Int(ConfigKeys.McpServers.ActivityWindowMinutes)),
        TimeSpan.FromMinutes(config.Int(ConfigKeys.McpServers.StartsWindowMinutes)),
        config.Int(ConfigKeys.McpServers.MaxInstances),
        config.Int(ConfigKeys.McpServers.MaxLogEntries),
        TimeSpan.FromMilliseconds(config.Int(ConfigKeys.McpServers.LogListMilliseconds)));
}
