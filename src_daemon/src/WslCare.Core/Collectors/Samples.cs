using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Collectors;

/// <summary>One filesystem's usage as <c>df</c> shows it (plan §4.4).</summary>
/// <param name="UsedBytes">Size − free blocks (the reserve counts as used, as in <c>df</c>).</param>
/// <param name="AvailableBytes">What an unprivileged writer may still use (<c>df</c>'s "Avail").</param>
public sealed record VolumeUsage(string Path, long TotalBytes, long UsedBytes, long AvailableBytes)
{
    /// <summary><c>df</c>'s Use%: used ÷ (used + available) — the reserve is neither, so it is left out of both.</summary>
    public double UsedPercent => UsedBytes + AvailableBytes > 0 ? Math.Round(100.0 * UsedBytes / (UsedBytes + AvailableBytes), 1) : 0;

    /// <summary>One <c>statvfs</c> (Linux) / <c>GetDiskFreeSpaceEx</c> (Windows) through the seam; no walk.</summary>
    public static Reading<VolumeUsage> Measure(IFileSystem files, string path) => files.MeasureVolume(path) switch
    {
        VolumeReadResult.Measured m when m.TotalBytes > 0 => Reading.Of(new VolumeUsage(path, m.TotalBytes, m.TotalBytes - m.FreeBytes, m.AvailableBytes)),
        VolumeReadResult.Measured => Reading.Missing<VolumeUsage>($"the filesystem holding {path} reports a size of 0"),
        VolumeReadResult.Unreadable u => Reading.Missing<VolumeUsage>($"the filesystem holding {path} could not be measured: {u.Reason}"),
        _ => throw new System.Diagnostics.UnreachableException("VolumeReadResult is a closed set"),
    };
}

/// <summary>What the distro side observes in one sample: VM memory, its processes, the containers'
/// cgroups, what is left unattributed, and <c>df /</c>.</summary>
public sealed record VmSample(
    Reading<MemorySnapshot> Memory,
    Reading<ProcessSnapshot> Processes,
    Reading<ContainerSet> Containers,
    Unattributed Unattributed,
    Reading<VolumeUsage> RootVolume);

/// <summary>The host's physical memory (<c>GlobalMemoryStatusEx</c>).</summary>
public sealed record HostMemory(long TotalBytes, long AvailableBytes);

/// <summary>What the Windows side observes in one sample — the minimal Windows probe of E2.S1: host
/// RAM, the system drive's free space, and the WSL VM's footprint as Windows sees it.</summary>
/// <param name="VmmemWorkingSetBytes">The working set of <c>vmmemWSL</c>, the process that IS the VM (plan §4.1).</param>
public sealed record HostSample(Reading<HostMemory> Memory, Reading<VolumeUsage> SystemDrive, Reading<long> VmmemWorkingSetBytes);

/// <summary>One fast sample (plan §6 <c>status</c>): files read, nothing started.</summary>
/// <param name="Elapsed">How long the sample took — the &lt; 2 s budget is checked against it.</param>
/// <param name="Vm">The distro side; unavailable on the Windows binary, with the reason.</param>
/// <param name="Host">The Windows side; unavailable on the Linux binary, with the reason.</param>
public sealed record ProbeSample(HostSide Side, DateTimeOffset SampledAt, TimeSpan Elapsed, Reading<VmSample> Vm, Reading<HostSample> Host);
