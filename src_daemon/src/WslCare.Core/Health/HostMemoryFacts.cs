using WslCare.Core.Collectors;

namespace WslCare.Core.Health;

/// <summary>
/// E15.S5: Windows' memory pressure as ONE set of facts, whichever road read it — the Windows binary's own sample
/// (<see cref="FromProbe"/>) or the distro's full run, through E15.S1's Windows disk script (<see cref="FromWindowsDisk"/>). One
/// pure rule (<c>Thresholds/HostRules</c>) judges both, so the two roads cannot judge the same figure two ways. Every figure is a
/// <see cref="Reading{T}"/>: a figure a road does not read is unavailable WITH the reason, never 0.
/// </summary>
/// <param name="CommitBytes">Windows' commit charge.</param>
/// <param name="CommitLimitBytes">Windows' commit limit.</param>
/// <param name="CompressionBytes">The Memory Compression process's working set.</param>
/// <param name="VmmemBytes">The WSL VM's working set as Windows sees it (<c>vmmemWSL</c>).</param>
/// <param name="HostTotalBytes">The host's physical memory, for the vmmem advice's text.</param>
/// <param name="WslConfig">The <c>.wslconfig</c> audit, for the vmmem advice's text — the same input the Windows status's
/// <c>host.vmmemAdvice</c> has, so the advice and the <c>host.vmmem</c> verdict say the same.</param>
public sealed record HostMemoryFacts(
    Reading<long> CommitBytes,
    Reading<long> CommitLimitBytes,
    Reading<long> CompressionBytes,
    Reading<long> VmmemBytes,
    Reading<long> HostTotalBytes,
    Reading<WslConfigAudit> WslConfig)
{
    /// <summary>E15.S10: what Windows Defender's engine used since the previous full run — the distro's full run only; the Windows binary's
    /// road says the distro reads it (<see cref="DefenderReadByTheDistro"/>).</summary>
    public Reading<DefenderUsage> Defender { get; init; } = Reading.Missing<DefenderUsage>(DefenderReadByTheDistro);

    /// <summary>Why the full run has no host RAM figure.</summary>
    public const string HostTotalReadByWindows = "the host's RAM is read by the Windows binary's status";

    /// <summary>Why the Windows binary has no Defender figure (E15.S10): the distro's full run reads it, from two reads of one process.</summary>
    public const string DefenderReadByTheDistro = "Windows Defender's CPU is read by the distro's full run (two reads of the same MsMpEng, a full run apart)";

    /// <summary>The Windows binary's road: its own <c>GlobalMemoryStatusEx</c> and process listing.</summary>
    public static HostMemoryFacts FromProbe(HostSample host, Reading<WslConfigAudit> wslConfig) =>
        new(
            host.Commit.Map(c => c.ChargeBytes),
            host.Commit.Map(c => c.LimitBytes),
            host.CompressionBytes,
            host.VmmemWorkingSetBytes,
            host.Memory.Map(m => m.TotalBytes),
            wslConfig);

    /// <summary>The distro's full-run road: the Windows disk script's <c>commit=</c>, <c>compression=</c> and — since E15.S10 §B — <c>vmmem=</c>
    /// lines. Only the host's RAM stays unread (the vmmem advice then names no total).</summary>
    public static HostMemoryFacts FromWindowsDisk(WindowsDiskSample disk, Reading<WslConfigAudit> wslConfig) =>
        new(
            disk.Facts.Bind(f => f.CommitBytes),
            disk.Facts.Bind(f => f.CommitLimitBytes),
            disk.Facts.Bind(f => f.CompressionBytes),
            disk.Facts.Bind(f => f.VmmemBytes),
            Reading.Missing<long>(HostTotalReadByWindows),
            wslConfig);
}
