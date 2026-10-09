using WslCare.Core.Config;
using WslCare.Core.Records;

namespace WslCare.Core.Status;

// The wire shape of `status --json` (plan §6), read by the extension. Every figure is either
// `"available": true` with its value, or `"available": false` with a `reason` — an unread figure is
// never written as 0 (plan §15b #7). The value members are nullable ONLY here, at the JSON edge, so
// that an unavailable figure carries no value key at all: the context writes with
// `DefaultIgnoreCondition = WhenWritingNull`. The domain behind it holds `Reading<T>`, never null.

/// <summary>A size in bytes, or why it is unknown.</summary>
public sealed record ByteFigure(bool Available, long? Bytes, string? Reason);

/// <summary>A number (seconds, percent), or why it is unknown.</summary>
public sealed record NumberFigure(bool Available, double? Value, string? Reason);

/// <summary>A text (a path), or why it is unknown.</summary>
public sealed record TextFigure(bool Available, string? Value, string? Reason);

/// <summary>The answer of <c>status --json</c>.</summary>
/// <param name="Side"><c>wsl</c> or <c>windows</c>: which binary answered.</param>
/// <param name="SampleMilliseconds">How long the fast sample took (the budget is &lt; 2 s).</param>
public sealed record StatusReport(
    int SchemaVersion,
    string Side,
    DateTimeOffset SampledAt,
    long SampleMilliseconds,
    bool ObserveOnly,
    IReadOnlyList<ConfigErrorReport> ConfigError,
    VmReport Vm,
    HostReport Host,
    SlowReport Slow)
{
    /// <summary>User values this run did not take (plan §15q R1.2, R1.3, R1.6) — absent when there are none, so every older
    /// answer is unchanged.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Config.ConfigNoticeReport>? ConfigNotices { get; init; }

    /// <summary>The SHA-256 of the user configuration layer this answer read (plan §15q R1.7): a change made outside the
    /// extension shows as a new digest. Absent when there is no readable user layer.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? UserLayerDigest { get; init; }

    /// <summary>Container starts in the last 24 h (plan §4.3) from the follower's files — complete, or partial with the
    /// gaps named (plan §15b #0); absent when the answer was built without them.</summary>
    public Events.StartsWindow? ContainerStarts { get; init; }

    /// <summary>The daily folder sizes from the newest full run that measured them, with their age and growth.</summary>
    public Collect.FoldersReport? Folders { get; init; }

    /// <summary>Every threshold of plan §4 as <c>status</c> sees it (plan §15g B1): the SAME <see cref="Thresholds.Verdict"/>
    /// records and ids a full run writes into its detail — the sample's evaluated now, the full run's carried with their
    /// age (<see cref="Thresholds.Verdict.Basis"/>). An additive field (plan §6, §15g M2): absent from the sample a run
    /// detail embeds, and from every answer of a daemon older than E5.S0.</summary>
    public IReadOnlyList<Thresholds.Verdict>? Verdicts { get; init; }

    /// <summary>The build that answered — the same text <c>--version</c> prints (<c>0.1.0</c>, optionally
    /// <c>+&lt;commit&gt;</c>; <c>unknown</c> for an unstamped build). Additive (plan §15g B1, M2); absent from the sample
    /// a run detail embeds.</summary>
    public string? ProductVersion { get; init; }

    /// <summary>The action ids THIS side's registry holds, in <see cref="WslCare.Core.Actions.ActionId.ExecutionOrder"/> (plan §15f #3,
    /// §15j): the ids an <c>act</c> to this binary may name — the extension acts only on its own registry ∩ these. Additive
    /// (E6.S0); absent from the sample a run detail embeds.</summary>
    public IReadOnlyList<string>? Actions { get; init; }

    /// <summary>What this build can do beyond the 0.1.0 verbs (<see cref="WslCare.Core.Status.Capabilities"/>) — the AUTHORITY for acting
    /// (§15j M5). Additive (E6.S0).</summary>
    public IReadOnlyList<string>? Capabilities { get; init; }

    /// <summary>Which run is acting, queued, wedged or dead, read-only (§15j M3) — never a sweep. Additive (E6.S0).</summary>
    public RunningReport? Running { get; init; }

    /// <summary>The newest run with a cleanup (§15j M7). Additive (E6.S0).</summary>
    public LastCleanupReport? LastCleanup { get; init; }

    /// <summary>The daemon values the extension mirrors instead of copying (<see cref="StatusLimits"/>,
    /// <c>contracts/status-limits.json</c>). Additive (E7.S2c); absent from the sample a run detail embeds.</summary>
    public StatusLimits? Limits { get; init; }

    /// <summary>The MCP servers of the AI agents (plan §15q E7.S2d): how many run, which are idle, which burn CPU with no log
    /// write, how often each server started lately. Additive; the run detail's embedded sample carries it too.</summary>
    public McpServersReport? McpServers { get; init; }

    /// <summary>E14 S7a: the WINDOWS side's MCP server processes (<c>coai-mcp.exe</c>, <c>creds-mcp.exe</c>, the user's programs) —
    /// the Windows binary only; the distro's answer omits it. Additive, read-only.</summary>
    public WindowsMcpServersReport? WindowsMcpServers { get; init; }
}

/// <summary>The distro side (plan §4.1, §4.2, §4.4).</summary>
public sealed record VmReport(
    bool Available,
    string? Reason,
    MemoryReport? Memory,
    ProcessesReport? Processes,
    ContainersReport? Containers,
    UnattributedReport? Unattributed,
    VolumeReport? Disk);

public sealed record MemoryReport(
    bool Available,
    string? Reason,
    ByteFigure? Total,
    ByteFigure? MemAvailable,
    NumberFigure? AvailablePercent,
    ByteFigure? Free,
    ByteFigure? AnonPages,
    ByteFigure? InactiveAnon,
    ByteFigure? Shmem,
    ByteFigure? PageCache,
    ByteFigure? SwapTotal,
    ByteFigure? SwapUsed,
    FragmentationReport? Fragmentation,
    PressureReport? Pressure);

/// <summary>Free blocks of order ≥ 4 and ≥ 7 in the named zone, and the bytes they hold.</summary>
public sealed record FragmentationReport(
    bool Available,
    string? Reason,
    string? Zone,
    long? BlocksOrder4Plus,
    long? BlocksOrder7Plus,
    long? BytesOrder4Plus,
    long? BytesOrder7Plus);

public sealed record PressureReport(PsiReport Memory, PsiReport Io, PsiReport Cpu);

public sealed record PsiReport(bool Available, string? Reason, PsiLineReport? Some, PsiLineReport? Full);

public sealed record PsiLineReport(bool Available, string? Reason, double? Avg10, double? Avg60, double? Avg300, long? TotalMicroseconds);

public sealed record ProcessesReport(
    bool Available,
    string? Reason,
    int? Count,
    int? KernelThreads,
    int? Vanished,
    int? ContainerProcesses,
    long? HeldBytesTotal,
    IReadOnlyList<ProcessReport>? Top,
    IReadOnlyList<FamilyReport>? Families,
    IReadOnlyList<ProcessReport>? MntWalkers);

/// <param name="HeldBytes"><c>RssAnon</c> + <c>RssShmem</c>, the ranking key (plan §15b #4).</param>
public sealed record ProcessReport(
    int Pid,
    int Ppid,
    string User,
    string Name,
    string State,
    long RssAnonBytes,
    long RssShmemBytes,
    long HeldBytes,
    NumberFigure AgeSeconds,
    NumberFigure CpuSeconds,
    TextFigure Cwd,
    string CommandLine,
    string Family,
    bool Orphaned,
    bool HasTty,
    bool UnderMnt);

public sealed record FamilyReport(string Name, int Count, long HeldBytes);

public sealed record ContainersReport(
    bool Available,
    string? Reason,
    int? Count,
    long? MemoryCurrentTotal,
    long? AnonShmemTotal,
    IReadOnlyList<ContainerReport>? Items);

public sealed record ContainerReport(string Id, ByteFigure MemoryCurrent, ByteFigure AnonShmem);

/// <param name="State"><c>remainder</c>, <c>inconsistentSample</c> (the parts exceeded the whole — never a
/// negative number, plan §15b #4) or <c>unavailable</c>.</param>
public sealed record UnattributedReport(string State, long? Bytes, long? OvershootBytes, string? Reason);

public sealed record VolumeReport(bool Available, string? Reason, string? Path, long? TotalBytes, long? UsedBytes, long? AvailableBytes, double? UsedPercent);

/// <summary>The Windows side (plan §2): host RAM, the system drive, <c>vmmemWSL</c>; the <c>.vhdx</c> sizes
/// are named and marked unavailable until the Windows collectors read them.</summary>
public sealed record HostReport(
    bool Available,
    string? Reason,
    HostMemoryReport? Memory,
    VolumeReport? SystemDrive,
    ByteFigure? VmmemWorkingSet,
    ByteFigure? Vhdx)
{
    /// <summary>E14 S7a: when vmmemWSL holds more than <c>wslConfig.vmmemAdviceGb</c>, what to do about it — TEXT, never acted on.</summary>
    public string? VmmemAdvice { get; init; }
}

public sealed record HostMemoryReport(bool Available, string? Reason, long? TotalBytes, long? AvailableBytes);

/// <summary>The slow parts, from the last full run, with their age (plan §6, §15b #5).</summary>
public sealed record SlowReport(SlowPartReport ContainerStats, SlowPartReport WindowsClock);

public sealed record SlowPartReport(
    bool Available,
    string? Reason,
    string? RunId,
    DateTimeOffset? SampledAt,
    double? AgeSeconds,
    IReadOnlyList<ContainerStat>? Containers,
    double? OffsetSeconds,
    double? LaunchLatencySeconds);
