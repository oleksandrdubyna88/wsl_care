using System.Globalization;
using System.Text;

using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Collectors;

/// <summary>
/// A SYNTHETIC procfs / cgroup tree, in the kernel's file formats, for the edges the 2026-10-02 capture
/// cannot show: a container member visible in the distro's <c>/proc</c> (Docker Engine in the distro),
/// a child of <c>systemd --user</c>, a remainder that goes negative. The auxiliary vector is the captured
/// one, so ticks and page size are the real machine's.
/// </summary>
internal sealed class SyntheticProcTree : IDisposable
{
    public const long BootUnixSeconds = 1_790_948_339;

    private readonly TempRoot _root = new("proc");

    public SyntheticProcTree()
    {
        Paths = ProcfsFixture.PathsAt(_root.Path);
        Files = new PhysicalFileSystem(Paths);
        Directory.CreateDirectory(_root.Under("proc/self"));
        File.Copy(Path.Combine(ProcfsFixture.Root, "proc", "self", "auxv"), _root.Under("proc/self/auxv"));
        _root.File("proc/stat", $"cpu  1 2 3 4\nbtime {BootUnixSeconds}\n");
        _root.File("etc/passwd", "root:x:0:0::/root:/bin/bash\nme:x:1000:1000::/home/me:/bin/bash\n");
        Directory.CreateDirectory(_root.Under("sys/fs/cgroup"));
    }

    public LinuxHostPaths Paths { get; }

    public IFileSystem Files { get; }

    public SyntheticProcTree MemInfo(long anonKib, long shmemKib, long totalKib = 1_000_000)
    {
        _root.File("proc/meminfo", $"MemTotal: {totalKib} kB\nMemFree: 1 kB\nMemAvailable: {totalKib / 2} kB\nBuffers: 0 kB\nCached: 0 kB\nInactive(anon): 0 kB\nSwapTotal: 0 kB\nSwapFree: 0 kB\nAnonPages: {anonKib} kB\nShmem: {shmemKib} kB\n");
        return this;
    }

    /// <summary>One process. <paramref name="rssFileKib"/> is the page-cache share VmRSS carries and the
    /// held figure must NOT: a test that sums VmRSS sees it.</summary>
    /// <param name="words">The argv word by word, when a word itself holds a space (plan §15q E7.S2d C-2); else <paramref name="argv"/>
    /// split on spaces.</param>
    /// <param name="cpuTicks">utime + stime (split evenly).</param>
    public SyntheticProcTree Process(int pid, int ppid, string cgroup, long rssAnonKib, long rssShmemKib = 0, long rssFileKib = 0, string argv = "", int uid = 1000, int tty = 0, long startTicks = 100, IReadOnlyList<string>? words = null, long cpuTicks = 100)
    {
        var argvWords = words ?? (argv.Length == 0 ? [] : argv.Split(' '));
        var name = argvWords.Count == 0 ? $"p{pid}" : Path.GetFileName(argvWords[0]);
        var vmRss = rssAnonKib + rssShmemKib + rssFileKib;
        _root.File($"proc/{pid}/status", string.Create(CultureInfo.InvariantCulture,
            $"Name:\t{name}\nState:\tS (sleeping)\nPPid:\t{ppid}\nUid:\t{uid}\t{uid}\t{uid}\t{uid}\nKthread:\t0\nVmRSS:\t{vmRss} kB\nRssAnon:\t{rssAnonKib} kB\nRssFile:\t{rssFileKib} kB\nRssShmem:\t{rssShmemKib} kB\n"));
        Stat(pid, ppid, name, tty, startTicks, cpuTicks);
        _root.File($"proc/{pid}/cgroup", $"0::{cgroup}\n");
        File.WriteAllBytes(_root.Under($"proc/{pid}/cmdline"), Encoding.UTF8.GetBytes(string.Join('\0', argvWords.Count == 0 ? [name] : argvWords) + "\0"));
        return this;
    }

    /// <summary>Rewrites one process's <c>stat</c> — what a test does "during" a CPU window.</summary>
    public SyntheticProcTree Stat(int pid, int ppid, string name, int tty, long startTicks, long cpuTicks)
    {
        _root.File($"proc/{pid}/stat", string.Create(CultureInfo.InvariantCulture, $"{pid} ({name}) S {ppid} {pid} {pid} {tty} -1 0 0 0 0 0 {cpuTicks / 2} {cpuTicks - (cpuTicks / 2)} 0 0 20 0 1 0 {startTicks} 0 0\n"));
        return this;
    }

    /// <summary>A file under the tree at the DISTRO path <paramref name="distroPath"/>, last written at <paramref name="modifiedAt"/>.</summary>
    public string FileAt(string distroPath, DateTimeOffset modifiedAt)
    {
        var path = _root.File(distroPath.TrimStart('/'), "x");
        File.SetLastWriteTimeUtc(path, modifiedAt.UtcDateTime);
        return path;
    }

    /// <summary>Removes one process from the tree — it exited.</summary>
    public void Exit(int pid) => Directory.Delete(_root.Under($"proc/{pid}"), recursive: true);

    /// <summary>A container cgroup: the cgroupfs driver's <c>docker/&lt;id&gt;</c>, or with
    /// <paramref name="systemdDriver"/> the systemd driver's <c>system.slice/docker-&lt;id&gt;.scope</c>.</summary>
    public SyntheticProcTree Container(string id, long anonBytes, long shmemBytes, long memoryCurrentBytes, bool systemdDriver = false)
    {
        var dir = systemdDriver ? $"sys/fs/cgroup/system.slice/docker-{id}.scope" : $"sys/fs/cgroup/docker/{id}";
        _root.File($"{dir}/memory.current", $"{memoryCurrentBytes}\n");
        _root.File($"{dir}/memory.stat", $"anon {anonBytes}\nfile 123\nshmem {shmemBytes}\n");
        return this;
    }

    public void Dispose() => _root.Dispose();
}
