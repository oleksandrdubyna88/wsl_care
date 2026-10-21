using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Collectors;

/// <summary>The host counters the Windows probe asks the operating system for — a seam, so the probe's
/// assembly is a unit test on any OS and the real counters are read only by <see cref="Win32Counters"/>. Each reader fails
/// ALONE (E15.S5): its own figure is unavailable with the reason, and the others are still read.</summary>
public interface IWindowsCounters
{
    Reading<HostMemory> Memory();

    /// <summary>The summed working set of the processes named <c>vmmemWSL</c>; unavailable when none runs.</summary>
    Reading<long> VmmemWorkingSet();

    /// <summary>E15.S5: Windows' commit charge and commit limit (<c>GlobalMemoryStatusEx</c>'s page-file fields).</summary>
    Reading<HostCommit> Commit();

    /// <summary>E15.S5: the working set of the <c>Memory Compression</c> process; unavailable when there is none.</summary>
    Reading<long> CompressionWorkingSet();
}

/// <summary>
/// The minimal Windows probe of E2.S1 (plan §2, §4.1, §4.4): host RAM, the system drive, and the VM's
/// footprint as Windows sees it, so the <c>win-x64</c> binary answers <c>status --json</c> for its side.
/// The VM's own figures are the Linux binary's; the <c>.vhdx</c> sizes, the pool and the Windows AI-agent
/// folders arrive with the Windows collectors (E11, E7.S3).
/// </summary>
public sealed class WindowsProbe(IFileSystem files, WindowsHostPaths paths, IWindowsCounters counters, TimeProvider clock) : IHostProbe
{
    /// <summary>Why the VM's figures are not in the Windows binary's answer.</summary>
    public const string VmIsTheOtherBinary =
        "the VM's memory, processes and df / are read by wsl-care inside the distro (wsl.exe -d <distro> -- /opt/wsl-care/bin/wsl-care status --json), not from Windows";

    public HostSide Side => HostSide.Windows;

    public ProbeSample Sample(CancellationToken cancellationToken)
    {
        var started = clock.GetTimestamp();
        var sampledAt = clock.GetUtcNow();
        cancellationToken.ThrowIfCancellationRequested();
        var host = new HostSample(
            counters.Memory(),
            VolumeUsage.Measure(files, paths.SystemDrive),
            counters.VmmemWorkingSet(),
            counters.Commit(),
            counters.CompressionWorkingSet());
        return new ProbeSample(Side, sampledAt, clock.GetElapsedTime(started), Reading.Missing<VmSample>(VmIsTheOtherBinary), Reading.Of(host));
    }
}

/// <summary>
/// The real counters: <c>GlobalMemoryStatusEx</c>, and the process list the kernel already keeps
/// (<see cref="Process.GetProcessesByName(string)"/> reads a snapshot — it STARTS nothing, and the
/// working set comes from that snapshot, so no handle to the protected <c>vmmemWSL</c> or <c>Memory Compression</c> is opened).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class Win32Counters : IWindowsCounters
{
    private const string VmProcessName = "vmmemWSL";

    private const string CompressionProcessName = "Memory Compression";

    public Reading<HostMemory> Memory() =>
        Status().Map(status => new HostMemory((long)status.TotalPhysical, (long)status.AvailablePhysical));

    public Reading<HostCommit> Commit() =>
        Status().Map(status => HostCommit.FromPageFile(status.TotalPageFile, status.AvailablePageFile));

    public Reading<long> VmmemWorkingSet() =>
        ProcessWorkingSets.Sum(VmProcessName, () => Process.GetProcessesByName(VmProcessName), p => p.WorkingSet64, $"no {VmProcessName} process: the WSL VM is not running");

    public Reading<long> CompressionWorkingSet() =>
        ProcessWorkingSets.Sum(CompressionProcessName, () => Process.GetProcessesByName(CompressionProcessName), p => p.WorkingSet64, $"no {CompressionProcessName} process: Windows does not compress memory now");

    /// <summary>One <c>GlobalMemoryStatusEx</c> call — each reader makes its own, so a failure costs that reader's figure only.</summary>
    private static Reading<MemoryStatusEx> Status()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status)
            ? Reading.Of(status)
            : Reading.Missing<MemoryStatusEx>($"GlobalMemoryStatusEx failed with Win32 error {Marshal.GetLastPInvokeError()}");
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// <summary><c>MEMORYSTATUSEX</c>, field for field.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
