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
    SlowReport Slow);

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
    ByteFigure? Vhdx);

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
