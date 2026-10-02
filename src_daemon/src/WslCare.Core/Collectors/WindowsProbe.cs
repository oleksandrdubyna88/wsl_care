using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Collectors;

/// <summary>The two host counters the Windows probe asks the operating system for — a seam, so the probe's
/// assembly is a unit test on any OS and the real counters are read only by <see cref="Win32Counters"/>.</summary>
public interface IWindowsCounters
{
    Reading<HostMemory> Memory();

    /// <summary>The summed working set of the processes named <c>vmmemWSL</c>; unavailable when none runs.</summary>
    Reading<long> VmmemWorkingSet();
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
        var host = new HostSample(counters.Memory(), VolumeUsage.Measure(files, paths.SystemDrive), counters.VmmemWorkingSet());
        return new ProbeSample(Side, sampledAt, clock.GetElapsedTime(started), Reading.Missing<VmSample>(VmIsTheOtherBinary), Reading.Of(host));
    }
}

/// <summary>
/// The real counters: <c>GlobalMemoryStatusEx</c>, and the process list the kernel already keeps
/// (<see cref="Process.GetProcessesByName(string)"/> reads a snapshot — it STARTS nothing, and the
/// working set comes from that snapshot, so no handle to the protected <c>vmmemWSL</c> is opened).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class Win32Counters : IWindowsCounters
{
    private const string VmProcessName = "vmmemWSL";

    public Reading<HostMemory> Memory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status)
            ? Reading.Of(new HostMemory((long)status.TotalPhysical, (long)status.AvailablePhysical))
            : Reading.Missing<HostMemory>($"GlobalMemoryStatusEx failed with Win32 error {Marshal.GetLastPInvokeError()}");
    }

    public Reading<long> VmmemWorkingSet()
    {
        var processes = Process.GetProcessesByName(VmProcessName);
        try
        {
            return processes.Length == 0
                ? Reading.Missing<long>($"no {VmProcessName} process: the WSL VM is not running")
                : Reading.Of(processes.Sum(p => p.WorkingSet64));
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
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
