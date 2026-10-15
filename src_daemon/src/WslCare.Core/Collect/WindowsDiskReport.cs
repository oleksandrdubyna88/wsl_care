using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Health;
using WslCare.Core.Status;

namespace WslCare.Core.Collect;

// The wire shape of the Windows disk sample in a run detail (E15.S1, PLAN_disk_health.md). As everywhere at the JSON edge, a
// figure is "available": true with its value or "available": false with a reason and no value; a section the script could not
// read says why; the whole sample unavailable carries one reason and no section. No verdict judges it yet — later stories read it.

/// <summary>A virtual disk file: a distro's or Docker's data disk.</summary>
public sealed record VirtualDiskReport(string Name, string Path, ByteFigure Size, FlagFigure Sparse, InstantFigure LastWrite);

public sealed record VirtualDisksReport(bool Available, string? Reason, IReadOnlyList<VirtualDiskReport>? Disks);

public sealed record DockerDataDiskReport(bool Available, string? Reason, VirtualDiskReport? Disk);

/// <param name="DiskNumber">The physical disk the volume is a partition of.</param>
public sealed record WindowsVolumeReport(string Letter, CountFigure DiskNumber, string FileSystem, ByteFigure Size, ByteFigure Free, string Label);

public sealed record WindowsVolumesReport(bool Available, string? Reason, IReadOnlyList<WindowsVolumeReport>? Volumes);

/// <summary>A physical disk on the wire — its own record, so a change to the domain record cannot change a saved detail.</summary>
public sealed record PhysicalDiskReport(int Number, string Bus, string Health, string OperationalStatus, string Firmware, string Model);

public sealed record PhysicalDisksReport(bool Available, string? Reason, IReadOnlyList<PhysicalDiskReport>? Disks);

public sealed record ReliabilityCounterReport(int Number, NumberFigure TemperatureCelsius, NumberFigure WearPercent, NumberFigure ReadErrors, NumberFigure WriteErrors, NumberFigure PowerOnHours);

/// <summary>The counters, or the refusal — unelevated (always, wsl-care never elevates) the refusal is the answer.</summary>
public sealed record ReliabilityReport(bool Available, string? Reason, IReadOnlyList<ReliabilityCounterReport>? Disks);

/// <param name="Day">The UTC calendar day, <c>yyyy-MM-dd</c>.</param>
/// <param name="Disk">The <c>HarddiskN</c> the events name.</param>
public sealed record StorageEventGroupReport(string Day, string Provider, int Id, CountFigure Disk, int Count);

/// <param name="Capped">The cap was reached: the counts are a lower bound over the newest events.</param>
/// <param name="GroupsTotal">How many groups the script counted — more than <paramref name="Groups"/> holds when it cut them.</param>
/// <param name="GroupsCut">The list stops at <c>disk.eventGroupsMax</c>: older groups were left out, not absent.</param>
public sealed record StorageEventsReport(bool Available, string? Reason, int? Read, int? MaxRead, int? SinceDays, bool? Capped, IReadOnlyList<StorageEventGroupReport>? Groups, int? GroupsTotal, bool? GroupsCut);

public sealed record WindowsIdentityReport(TextFigure Edition, TextFigure DisplayVersion, TextFigure Build, InstantFigure InstalledAt, TextFigure MachineGuid, TextFigure Account);

public sealed record DisplayAdapterReport(string Name, string PnpDeviceId, CountFigure ProblemCode);

public sealed record DisplayAdaptersReport(bool Available, string? Reason, IReadOnlyList<DisplayAdapterReport>? Adapters);

/// <summary>The PowerShell the Windows disk script ran under (E15.S1b): the program the daemon started — the file the runner started
/// from the system drive (the MSI pwsh as the drive's mount names it), else the bare program — and the sentence the choice rested on.</summary>
public sealed record InteropEngineReport(string Program, string Reason);

/// <summary>The Windows disk facts one full run read through the second fixed script (E15.S1).</summary>
/// <param name="ElapsedSeconds">How long the script ran — present only for an answer that was read.</param>
/// <param name="MalformedLines">Lines the parser counted and skipped.</param>
public sealed record WindowsDiskReport(
    bool Available,
    string? Reason,
    DateTimeOffset SampledAt,
    NumberFigure ElapsedSeconds,
    int? MalformedLines,
    VirtualDisksReport? Distros,
    DockerDataDiskReport? DockerDataDisk,
    WindowsVolumesReport? Volumes,
    PhysicalDisksReport? PhysicalDisks,
    ReliabilityReport? Reliability,
    StorageEventsReport? Events,
    ByteFigure? CommitBytes,
    ByteFigure? CommitLimitBytes,
    ByteFigure? CompressionBytes,
    WindowsIdentityReport? Identity,
    DisplayAdaptersReport? Adapters)
{
    /// <summary>The PowerShell the script ran under, and why (E15.S1b); absent on the Windows binary and on a detail written
    /// before the story.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public InteropEngineReport? Engine { get; init; }
}

/// <summary>The domain sample turned into its wire shape — the one place it becomes available + value or reason.</summary>
public static class WindowsDiskReports
{
    public static WindowsDiskReport From(WindowsDiskSample sample) => Facts(sample) with { Engine = Engine(sample.Engine, sample.StartedFrom) };

    /// <summary>The engine on the wire; its program is the file the runner REALLY started when it came from the system drive (code
    /// round: one source), else the bare program the policy judged.</summary>
    private static InteropEngineReport? Engine(Reading<InteropEngine> engine, string startedFrom) =>
        engine is Reading<InteropEngine>.Available { Value: var e } ? new(startedFrom.Length > 0 ? startedFrom : e.Program, e.Reason) : null;

    private static WindowsDiskReport Facts(WindowsDiskSample sample)
    {
        var elapsed = StatusReports.Number(sample.Elapsed.Map(e => Math.Round(e.TotalSeconds, 3)));
        return sample.Facts is Reading<WindowsDiskFacts>.Available { Value: var f }
            ? new(true, null, sample.SampledAt, elapsed, f.MalformedLines, Disks(f.Distros), Docker(f.DockerDataDisk), Volumes(f.Volumes), Physical(f.PhysicalDisks),
                Reliability(f.Reliability), Events(f.Events), StatusReports.Bytes(f.CommitBytes), StatusReports.Bytes(f.CommitLimitBytes), StatusReports.Bytes(f.CompressionBytes),
                Identity(f.Identity), Adapters(f.Adapters))
            : new(false, sample.Facts.ReasonOrEmpty, sample.SampledAt, elapsed, null, null, null, null, null, null, null, null, null, null, null, null);
    }

    private static VirtualDiskReport Disk(WindowsVirtualDisk d) =>
        new(d.Name, d.Path, StatusReports.Bytes(d.SizeBytes), Flag(d.Sparse), Instant(d.LastWriteUtc));

    private static VirtualDisksReport Disks(Reading<IReadOnlyList<WindowsVirtualDisk>> disks) =>
        disks is Reading<IReadOnlyList<WindowsVirtualDisk>>.Available { Value: var list } ? new(true, null, [.. list.Select(Disk)]) : new(false, disks.ReasonOrEmpty, null);

    private static DockerDataDiskReport Docker(Reading<WindowsVirtualDisk> disk) =>
        disk is Reading<WindowsVirtualDisk>.Available { Value: var d } ? new(true, null, Disk(d)) : new(false, disk.ReasonOrEmpty, null);

    private static WindowsVolumesReport Volumes(Reading<IReadOnlyList<WindowsVolume>> volumes) =>
        volumes is Reading<IReadOnlyList<WindowsVolume>>.Available { Value: var list }
            ? new(true, null, [.. list.Select(v => new WindowsVolumeReport(v.Letter, Count(v.DiskNumber), v.FileSystem, StatusReports.Bytes(v.SizeBytes), StatusReports.Bytes(v.FreeBytes), v.Label))])
            : new(false, volumes.ReasonOrEmpty, null);

    private static PhysicalDisksReport Physical(Reading<IReadOnlyList<WindowsPhysicalDisk>> disks) =>
        disks is Reading<IReadOnlyList<WindowsPhysicalDisk>>.Available { Value: var list }
            ? new(true, null, [.. list.Select(d => new PhysicalDiskReport(d.Number, d.Bus, d.Health, d.OperationalStatus, d.Firmware, d.Model))])
            : new(false, disks.ReasonOrEmpty, null);

    private static ReliabilityReport Reliability(Reading<IReadOnlyList<StorageReliability>> counters) =>
        counters is Reading<IReadOnlyList<StorageReliability>>.Available { Value: var list }
            ? new(true, null, [.. list.Select(c => new ReliabilityCounterReport(c.Number, Number(c.TemperatureCelsius), Number(c.WearPercent), Number(c.ReadErrors), Number(c.WriteErrors), Number(c.PowerOnHours)))])
            : new(false, counters.ReasonOrEmpty, null);

    private static StorageEventsReport Events(Reading<StorageEvents> events) =>
        events is Reading<StorageEvents>.Available { Value: var e }
            ? new(true, null, e.Read, e.MaxRead, e.SinceDays, e.Capped, [.. e.Groups.Select(g => new StorageEventGroupReport(g.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), g.Provider, g.Id, Count(g.Disk), g.Count))], e.GroupsTotal, e.GroupsCut)
            : new(false, events.ReasonOrEmpty, null, null, null, null, null, null, null);

    private static WindowsIdentityReport Identity(WindowsIdentity i) =>
        new(Text(i.Edition), Text(i.DisplayVersion), Text(i.Build), Instant(i.InstalledAt), Text(i.MachineGuid), Text(i.Account));

    private static DisplayAdaptersReport Adapters(Reading<IReadOnlyList<DisplayAdapter>> adapters) =>
        adapters is Reading<IReadOnlyList<DisplayAdapter>>.Available { Value: var list }
            ? new(true, null, [.. list.Select(a => new DisplayAdapterReport(a.Name, a.PnpDeviceId, Count(a.ProblemCode)))])
            : new(false, adapters.ReasonOrEmpty, null);

    private static CountFigure Count(Reading<int> count) => HealthReports.Count(count);

    private static NumberFigure Number(Reading<long> number) => StatusReports.Number(number.Map(n => (double)n));

    private static FlagFigure Flag(Reading<bool> flag) => HealthReports.Flag(flag);

    private static InstantFigure Instant(Reading<DateTimeOffset> instant) => HealthReports.Instant(instant);

    private static TextFigure Text(Reading<string> text) => StatusReports.Text(text);
}
