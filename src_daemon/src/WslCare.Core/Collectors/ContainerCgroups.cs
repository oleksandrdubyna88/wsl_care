using System.Globalization;

using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Files;

namespace WslCare.Core.Collectors;

/// <summary>
/// One container's memory, read from its cgroup — never from <c>docker stats</c>, which is a slow
/// process and belongs to <c>collect</c> (plan §15b #5).
/// </summary>
/// <param name="Id">The 64-hex container id, as Docker names the cgroup.</param>
/// <param name="MemoryCurrentBytes"><c>memory.current</c>: everything charged to the container, page
/// cache and kernel memory included — the figure the panel shows as the container's memory (plan §4.2).</param>
/// <param name="AnonShmemBytes"><c>memory.stat</c> <c>anon</c> + <c>shmem</c>: the container's share of
/// <c>AnonPages</c> + <c>Shmem</c>, which is what the unattributed arithmetic subtracts (see
/// <see cref="Attribution"/> for why it is not <c>memory.current</c>).</param>
public sealed record ContainerMemory(string Id, Reading<long> MemoryCurrentBytes, Reading<long> AnonShmemBytes);

/// <summary>Every container cgroup found. Empty is an answer (no container runs); a missing cgroup
/// mount is not — that is a <see cref="Reading{T}.Unavailable"/>.</summary>
public sealed record ContainerSet(IReadOnlyList<ContainerMemory> Containers)
{
    /// <summary>The ids whose anon + shmem was read — the containers the arithmetic counts, and so the
    /// ones whose processes are NOT counted again as distro processes (plan §15b #4).</summary>
    public IReadOnlySet<string> CountedIds { get; } =
        Containers.Where(c => c.AnonShmemBytes.IsAvailable).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

    public long AnonShmemTotal => Containers.Sum(c => c.AnonShmemBytes.ValueOr(0));

    public long MemoryCurrentTotal => Containers.Sum(c => c.MemoryCurrentBytes.ValueOr(0));
}

/// <summary>
/// Where Docker puts a container's cgroup, and how a process is recognised as one of its members.
/// </summary>
/// <remarks>Two layouts, both cgroup v2: the <c>cgroupfs</c> driver's <c>/docker/&lt;id&gt;</c> — what
/// Docker Desktop's containers appear as from inside <c>Ubuntu</c>, measured 2026-10-02 (ten such
/// directories under <c>/sys/fs/cgroup/docker</c>, none of their processes in Ubuntu's <c>/proc</c>,
/// which is a separate PID namespace) — and the <c>systemd</c> driver's
/// <c>/system.slice/docker-&lt;id&gt;.scope</c>, which a Docker Engine installed in the distro uses and
/// whose processes DO appear in the distro's <c>/proc</c>.</remarks>
public static class ContainerCgroups
{
    private const int IdLength = 64;
    private const string ScopePrefix = "docker-";
    private const string ScopeSuffix = ".scope";

    /// <summary>The container a process belongs to, from its <c>/proc/[pid]/cgroup</c> path; empty when none.</summary>
    public static string ContainerIdOf(string cgroupPath)
    {
        var segments = cgroupPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var underDocker = segments.Zip(segments.Skip(1)).Where(p => p.First == "docker" && IsId(p.Second)).Select(p => p.Second);
        return underDocker.Concat(segments.Select(ScopeId)).FirstOrDefault(id => id.Length > 0, string.Empty);
    }

    /// <summary>Every container cgroup under <paramref name="cgroupRoot"/>, with its memory.</summary>
    public static Reading<ContainerSet> Read(IFileSystem files, string cgroupRoot)
    {
        if (!files.DirectoryExists(cgroupRoot))
        {
            return Reading.Missing<ContainerSet>($"{cgroupRoot} does not exist: no cgroup v2 mount to read container memory from");
        }

        var cgroupfs = files.ListDirectories(Join(cgroupRoot, "docker")).Where(d => IsId(ProcText.LastSegment(d)));
        var systemd = files.ListDirectories(Join(cgroupRoot, "system.slice")).Where(d => ScopeId(ProcText.LastSegment(d)).Length > 0);
        return Reading.Of(new ContainerSet([.. cgroupfs.Concat(systemd).Select(d => ReadOne(files, d))]));
    }

    /// <summary><c>memory.stat</c>'s byte counters (cgroup v2 writes them in bytes, not pages).</summary>
    public static Reading<long> AnonPlusShmem(string memoryStat, string path)
    {
        var counters = ProcText.Lines(memoryStat)
            .Select(l => l.Split(' '))
            .Where(f => f.Length == 2 && long.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .ToDictionary(f => f[0], f => long.Parse(f[1], CultureInfo.InvariantCulture), StringComparer.Ordinal);
        return counters.TryGetValue("anon", out var anon) && counters.TryGetValue("shmem", out var shmem)
            ? Reading.Of(anon + shmem)
            : Reading.Missing<long>($"{path} has no anon or shmem counter");
    }

    private static ContainerMemory ReadOne(IFileSystem files, string directory)
    {
        var name = ProcText.LastSegment(directory);
        var id = IsId(name) ? name : ScopeId(name);
        var current = Join(directory, "memory.current");
        var stat = Join(directory, "memory.stat");
        return new ContainerMemory(
            id,
            ProcText.Read(files, current).Bind(text => ParseBytes(text, current)),
            ProcText.Read(files, stat).Bind(text => AnonPlusShmem(text, stat)));
    }

    private static Reading<long> ParseBytes(string text, string path) =>
        long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)
            ? Reading.Of(bytes)
            : Reading.Missing<long>($"{path} does not hold a byte count");

    private static string ScopeId(string segment) =>
        segment.StartsWith(ScopePrefix, StringComparison.Ordinal) && segment.EndsWith(ScopeSuffix, StringComparison.Ordinal)
        && IsId(segment[ScopePrefix.Length..^ScopeSuffix.Length])
            ? segment[ScopePrefix.Length..^ScopeSuffix.Length]
            : string.Empty;

    private static bool IsId(string text) => text.Length == IdLength && text.All(char.IsAsciiHexDigitLower);

    private static string Join(string root, string name) => $"{root.TrimEnd('/', '\\')}/{name}";
}
