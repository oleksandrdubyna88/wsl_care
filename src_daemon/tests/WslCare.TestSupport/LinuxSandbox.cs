using System.Text;

using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.TestSupport;

/// <summary>
/// The DISTRO's layout (<see cref="LinuxHostPaths"/>) over a temporary root, on whichever operating system runs the test,
/// with the real <see cref="PhysicalFileSystem"/> over it — for tests of what runs inside the distro (the action engine, A10,
/// the target user) that must run on the Windows legs too. Helpers write the files the distro would hold.
/// </summary>
public sealed class LinuxSandbox : IDisposable
{
    public LinuxSandbox(string purpose)
    {
        Root = new TempRoot(purpose);
        Paths = ProcfsFixture.PathsAt(Root.Path);
        Files = new PhysicalFileSystem(Paths) { TrustedStateOwner = RegularFiles.EffectiveUid() };
    }

    public TempRoot Root { get; }

    public LinuxHostPaths Paths { get; }

    public PhysicalFileSystem Files { get; }

    /// <summary>A file at the DISTRO path <paramref name="absoluteLinuxPath"/> (<c>/etc/passwd</c>), as this process sees it.</summary>
    public string Write(string absoluteLinuxPath, string content)
    {
        var path = Paths.DistroPath(absoluteLinuxPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    /// <summary>A file of <paramref name="bytes"/> bytes, last written at <paramref name="modifiedAt"/>.</summary>
    public string Sized(string absoluteLinuxPath, int bytes, DateTimeOffset modifiedAt)
    {
        var path = Write(absoluteLinuxPath, new string('x', bytes));
        File.SetLastWriteTimeUtc(path, modifiedAt.UtcDateTime);
        return path;
    }

    /// <summary>An executable named <paramref name="name"/> in the DISTRO folder <paramref name="folder"/>: <c>.exe</c> on Windows
    /// (what the resolver looks for there), an execute bit on Linux. Its content is never run.</summary>
    public string Executable(string folder, string name)
    {
        var path = Write($"{folder.TrimEnd('/')}/{name}{(OperatingSystem.IsWindows() ? ".exe" : string.Empty)}", "#!/bin/false\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    /// <summary>The CPU figures the idle gate reads: <c>/proc/loadavg</c> and <c>/proc/stat</c>'s cpuN lines.</summary>
    public LinuxSandbox Load(double load1, double load5, double load15, int cpus)
    {
        Write("/proc/loadavg", FormattableString.Invariant($"{load1:0.00} {load5:0.00} {load15:0.00} 1/123 4567\n"));
        Write("/proc/stat", "cpu  1 2 3 4\n" + string.Concat(Enumerable.Range(0, cpus).Select(i => $"cpu{i} 1 2 3 4\n")) + "btime 1790948339\n");
        return this;
    }

    /// <summary>The memory figures A1 / A2 read (E3.S3): <c>/proc/meminfo</c> with <paramref name="availableKib"/> of
    /// <paramref name="totalKib"/> available and a page cache of <paramref name="cachedKib"/> (+ 0 buffers), and
    /// <c>/proc/buddyinfo</c> whose zone Normal holds <paramref name="order7Blocks"/> free order-7 blocks and 50 order-4 ones —
    /// with the captured <c>/proc/self/auxv</c>, so the page size is the kernel's own.</summary>
    public LinuxSandbox Memory(long totalKib, long availableKib, long cachedKib, long order7Blocks)
    {
        Write("/proc/meminfo", FormattableString.Invariant($"MemTotal: {totalKib} kB\nMemFree: 1000 kB\nMemAvailable: {availableKib} kB\nBuffers: 0 kB\nCached: {cachedKib} kB\nSwapTotal: 0 kB\nSwapFree: 0 kB\nAnonPages: 1000 kB\nShmem: 0 kB\nInactive(anon): 0 kB\n"));
        Write("/proc/buddyinfo", FormattableString.Invariant($"Node 0, zone      DMA      1      1      1      1      1      1      1      1      1      1      1\nNode 0, zone   Normal    500    400    300    200     50      0      0 {order7Blocks}      0      0      0\n"));
        var auxv = Paths.DistroPath("/proc/self/auxv");
        Directory.CreateDirectory(Path.GetDirectoryName(auxv)!);
        File.Copy(Path.Combine(ProcfsFixture.Root, "proc", "self", "auxv"), auxv, overwrite: true);
        return this;
    }

    public void Dispose() => Root.Dispose();
}
