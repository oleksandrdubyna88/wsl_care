using WslCare.Core.Collectors;

namespace WslCare.Core.Health;

/// <summary>A virtual disk file on the Windows side: a registered distro's (named by <c>Lxss</c>) or Docker's data disk.</summary>
/// <param name="Name">The distro's name; <c>docker-data</c> for Docker's data disk.</param>
/// <param name="Path">The file as Windows names it (<c>C:\…\ext4.vhdx</c>).</param>
/// <param name="SizeBytes">The file's size on the host — or why not (the file is not there).</param>
/// <param name="Sparse">Whether the file carries the sparse attribute.</param>
/// <param name="LastWriteUtc">The file's last write, UTC.</param>
public sealed record WindowsVirtualDisk(string Name, string Path, Reading<long> SizeBytes, Reading<bool> Sparse, Reading<DateTimeOffset> LastWriteUtc);

/// <summary>A lettered Windows volume and the physical disk it is a partition of.</summary>
/// <param name="Letter">The drive letter, without the colon.</param>
/// <param name="DiskNumber">The physical disk's number (<c>Get-Partition</c>), or why not.</param>
/// <param name="Label">The volume's label, as printed (empty when it has none).</param>
public sealed record WindowsVolume(string Letter, Reading<int> DiskNumber, string FileSystem, Reading<long> SizeBytes, Reading<long> FreeBytes, string Label);

/// <summary>A physical disk as <c>Get-PhysicalDisk</c> names it.</summary>
public sealed record WindowsPhysicalDisk(int Number, string Bus, string Health, string OperationalStatus, string Firmware, string Model);

/// <summary>One disk's reliability counters — read only when the script runs elevated, which wsl-care never asks for.</summary>
public sealed record StorageReliability(int Number, Reading<long> TemperatureCelsius, Reading<long> WearPercent, Reading<long> ReadErrors, Reading<long> WriteErrors, Reading<long> PowerOnHours);

/// <summary>How many System-log storage events of one UTC day, provider, id and disk the script counted.</summary>
/// <param name="Disk">The <c>\Device\HarddiskN</c> the event names (its <c>N</c>), or why not (the event names none).</param>
public sealed record StorageEventGroup(DateTimeOffset Day, string Provider, int Id, Reading<int> Disk, int Count);

/// <summary>The storage events of the window, aggregated inside the script.</summary>
/// <param name="Read">How many events were read in all.</param>
/// <param name="MaxRead">The most the script reads (<c>disk.eventsMaxRead</c>); reaching it means older events were not read.</param>
/// <param name="SinceDays">The window (<c>disk.eventsSinceDays</c>).</param>
public sealed record StorageEvents(int Read, int MaxRead, int SinceDays, IReadOnlyList<StorageEventGroup> Groups)
{
    /// <summary>The cap was reached: the counts are a lower bound over the newest events.</summary>
    public bool Capped => Read >= MaxRead;

    /// <summary>How many groups the script counted — more than it printed when its cap (<c>disk.eventGroupsMax</c>) was reached.</summary>
    public int GroupsTotal { get; init; } = Groups.Count;

    /// <summary>Fewer groups were printed than counted: the oldest groups are not in <see cref="Groups"/>.</summary>
    public bool GroupsCut => GroupsTotal > Groups.Count;
}

/// <summary>A display adapter and its Device Manager problem code (0 = working) — the GPU story's input (G2).</summary>
public sealed record DisplayAdapter(Reading<int> ProblemCode, string PnpDeviceId, string Name);

/// <summary>The machine's identity — what every statistics record will carry (PLAN_disk_health.md §3.2).</summary>
/// <param name="Build">The build with its update revision (<c>26300.9550</c>).</param>
/// <param name="InstalledAt">Windows' install date (the registry's unix seconds), UTC.</param>
/// <param name="Account">The Windows account the script ran as (its SID): interop starts Windows programs as the Windows user who
/// started the VM, so <c>HKCU…Lxss</c> is THAT account's hive — recorded so a run under another account is visible (E15.S1's
/// plan round).</param>
public sealed record WindowsIdentity(Reading<string> Edition, Reading<string> DisplayVersion, Reading<string> Build, Reading<DateTimeOffset> InstalledAt, Reading<string> MachineGuid, Reading<string> Account);

/// <summary>
/// Everything one answer of the Windows disk script said (<see cref="WindowsDiskCommands"/>) — every figure a
/// <see cref="Reading{T}"/>, so a part the script could not read is unavailable WITH the reason, never 0.
/// </summary>
/// <param name="Distros">Every registered distro's virtual disk, or why the registry could not be read.</param>
/// <param name="DockerDataDisk">Docker Desktop's data disk at its default path, or why not (not there, not read).</param>
/// <param name="Reliability">The counters, or the refusal (unelevated, always — D4).</param>
/// <param name="CommitBytes">Windows' commit charge.</param>
/// <param name="CommitLimitBytes">Windows' commit limit (it grows with the pagefile).</param>
/// <param name="CompressionBytes">The Memory Compression process's working set.</param>
/// <param name="MalformedLines">Lines the parser could not read — counted and skipped, never fatal.</param>
public sealed record WindowsDiskFacts(
    Reading<IReadOnlyList<WindowsVirtualDisk>> Distros,
    Reading<WindowsVirtualDisk> DockerDataDisk,
    Reading<IReadOnlyList<WindowsVolume>> Volumes,
    Reading<IReadOnlyList<WindowsPhysicalDisk>> PhysicalDisks,
    Reading<IReadOnlyList<StorageReliability>> Reliability,
    Reading<StorageEvents> Events,
    Reading<long> CommitBytes,
    Reading<long> CommitLimitBytes,
    Reading<long> CompressionBytes,
    WindowsIdentity Identity,
    Reading<IReadOnlyList<DisplayAdapter>> Adapters,
    int MalformedLines);

/// <summary>
/// One full run's look at the Windows disk facts (E15.S1): when it was taken, how long the script ran, and what it said — or
/// why there is nothing (the script was not started, did not answer within its ceiling, exited non-zero, or its answer was
/// cut: then the WHOLE sample is unavailable with that reason).
/// </summary>
public sealed record WindowsDiskSample(DateTimeOffset SampledAt, Reading<TimeSpan> Elapsed, Reading<WindowsDiskFacts> Facts)
{
    /// <summary>The whole sample unavailable for one reason.</summary>
    public static WindowsDiskSample Unavailable(DateTimeOffset at, string reason) =>
        new(at, Reading.Missing<TimeSpan>(reason), Reading.Missing<WindowsDiskFacts>(reason));
}
