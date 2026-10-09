using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Collectors;

/// <summary>PSI for the three resources (plan §4.1).</summary>
public sealed record PressureSet(Reading<Pressure> Memory, Reading<Pressure> Io, Reading<Pressure> Cpu);

/// <summary>
/// The VM-wide memory figures of plan §4.1. <c>/proc/meminfo</c> is VM-wide (plan §2): it counts the
/// other distro and every container too, which is why the process table cannot add up to it alone.
/// </summary>
/// <param name="PageCache"><c>Cached</c> + <c>Buffers</c> — what WSL never hands back to Windows by itself.</param>
/// <param name="SwapUsed"><c>SwapTotal</c> − <c>SwapFree</c>.</param>
/// <param name="Fragmentation">Free high-order blocks in zone Normal (<c>/proc/buddyinfo</c>).</param>
public sealed record MemorySnapshot(
    Reading<long> Total,
    Reading<long> Available,
    Reading<double> AvailablePercent,
    Reading<long> Free,
    Reading<long> AnonPages,
    Reading<long> InactiveAnon,
    Reading<long> Shmem,
    Reading<long> PageCache,
    Reading<long> SwapTotal,
    Reading<long> SwapUsed,
    Reading<Fragmentation> Fragmentation,
    PressureSet Pressure)
{
    /// <summary><c>Committed_AS</c> (E14 S5): what the kernel has PROMISED — Linux over-commits, so it is a warning of promises,
    /// never of use. An init property, so a snapshot built without it reads "not read".</summary>
    public Reading<long> Committed { get; init; } = Reading.Missing<long>("Committed_AS was not read");
}

/// <summary>Reads <c>/proc/meminfo</c>, <c>/proc/buddyinfo</c> and <c>/proc/pressure/*</c> through <see cref="IFileSystem"/>.</summary>
public sealed class MemoryCollector(IFileSystem files, LinuxHostPaths paths)
{
    public Reading<MemorySnapshot> Read(Reading<KernelFacts> kernel)
    {
        var meminfoPath = $"{paths.ProcRoot}/meminfo";
        return ProcText.Read(files, meminfoPath).Map(MemInfo.Parse).Map(info => Snapshot(info, ReadFragmentation(kernel), ReadPressures()));
    }

    private static MemorySnapshot Snapshot(MemInfo info, Reading<Fragmentation> fragmentation, PressureSet pressure)
    {
        var total = info.Bytes("MemTotal");
        var available = info.Bytes("MemAvailable");
        var swapTotal = info.Bytes("SwapTotal");
        var snapshot = new MemorySnapshot(
            total,
            available,
            Reading.Combine(available, total, (part, whole) => (part, whole)).Bind(x => Percent(x.part, x.whole)),
            info.Bytes("MemFree"),
            info.Bytes("AnonPages"),
            info.Bytes("Inactive(anon)"),
            info.Bytes("Shmem"),
            Reading.Combine(info.Bytes("Cached"), info.Bytes("Buffers"), (cached, buffers) => cached + buffers),
            swapTotal,
            Reading.Combine(swapTotal, info.Bytes("SwapFree"), (t, f) => t - f),
            fragmentation,
            pressure);
        return snapshot with { Committed = info.Bytes("Committed_AS") };
    }

    /// <summary>A share of a whole, one decimal; a zero whole is an unreadable kernel, not 0 %.</summary>
    private static Reading<double> Percent(long part, long whole) =>
        whole > 0 ? Reading.Of(Math.Round(100.0 * part / whole, 1)) : Reading.Missing<double>("/proc/meminfo reports MemTotal 0");

    private Reading<Fragmentation> ReadFragmentation(Reading<KernelFacts> kernel)
    {
        var buddy = ProcText.Read(files, $"{paths.ProcRoot}/buddyinfo").Map(BuddyInfo.Parse);
        return Reading.Combine(buddy, kernel, (zones, k) => (zones, k))
            .Bind(x => Fragmentation.Of(x.zones, Fragmentation.ThresholdZone, x.k.PageSizeBytes));
    }

    private PressureSet ReadPressures() => PressureFile.ReadSet(files, paths);
}
