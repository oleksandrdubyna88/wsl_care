using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;
using WslCare.Core.Hosting;
using WslCare.Core.Records;

namespace WslCare.Core.Status;

/// <summary>
/// The domain sample turned into the wire shape of <c>status --json</c> — one place where a
/// <see cref="Reading{T}"/> becomes <c>available</c> + value or <c>available: false</c> + reason.
/// </summary>
public static class StatusReports
{
    /// <summary>Named, so the panel can show the row, and honest about why it is empty.</summary>
    public const string VhdxNotCollected = "the .vhdx sizes are read by the Windows collectors, which this build does not have yet";

    public static StatusReport From(ProbeSample sample, LastSlowParts slow, ConfigLoadResult loaded) =>
        new(
            SchemaVersion.Current,
            sample.Side == HostSide.Wsl ? "wsl" : "windows",
            sample.SampledAt,
            (long)sample.Elapsed.TotalMilliseconds,
            loaded.IsObserveOnly,
            [.. loaded.Errors.Select(ConfigErrorReport.From)],
            Vm(sample.Vm),
            Host(sample.Host),
            new SlowReport(ContainerStats(slow.ContainerStats), WindowsClock(slow.WindowsClock)));

    public static ByteFigure Bytes(Reading<long> reading) => reading switch
    {
        Reading<long>.Available a => new ByteFigure(true, a.Value, null),
        _ => new ByteFigure(false, null, reading.ReasonOrEmpty),
    };

    public static NumberFigure Number(Reading<double> reading) => reading switch
    {
        Reading<double>.Available a => new NumberFigure(true, a.Value, null),
        _ => new NumberFigure(false, null, reading.ReasonOrEmpty),
    };

    private static TextFigure Text(Reading<string> reading) => reading switch
    {
        Reading<string>.Available a => new TextFigure(true, a.Value, null),
        _ => new TextFigure(false, null, reading.ReasonOrEmpty),
    };

    private static VmReport Vm(Reading<VmSample> reading) => reading switch
    {
        Reading<VmSample>.Available { Value: var vm } => new VmReport(true, null, Memory(vm.Memory), Processes(vm.Processes), Containers(vm.Containers), Unattributed(vm.Unattributed), Volume(vm.RootVolume)),
        _ => new VmReport(false, reading.ReasonOrEmpty, null, null, null, null, null),
    };

    private static MemoryReport Memory(Reading<MemorySnapshot> reading) => reading switch
    {
        Reading<MemorySnapshot>.Available { Value: var m } => new MemoryReport(
            true, null, Bytes(m.Total), Bytes(m.Available), Number(m.AvailablePercent), Bytes(m.Free), Bytes(m.AnonPages), Bytes(m.InactiveAnon),
            Bytes(m.Shmem), Bytes(m.PageCache), Bytes(m.SwapTotal), Bytes(m.SwapUsed), Fragmentation(m.Fragmentation), Pressure(m.Pressure)),
        _ => new MemoryReport(false, reading.ReasonOrEmpty, null, null, null, null, null, null, null, null, null, null, null, null),
    };

    private static FragmentationReport Fragmentation(Reading<Fragmentation> reading) => reading switch
    {
        Reading<Fragmentation>.Available { Value: var f } => new FragmentationReport(true, null, f.Zone, f.BlocksOrder4Plus, f.BlocksOrder7Plus, f.BytesOrder4Plus, f.BytesOrder7Plus),
        _ => new FragmentationReport(false, reading.ReasonOrEmpty, null, null, null, null, null),
    };

    private static PressureReport Pressure(PressureSet set) => new(Psi(set.Memory), Psi(set.Io), Psi(set.Cpu));

    private static PsiReport Psi(Reading<Pressure> reading) => reading switch
    {
        Reading<Pressure>.Available { Value: var p } => new PsiReport(true, null, PsiLine(Reading.Of(p.Some)), PsiLine(p.Full)),
        _ => new PsiReport(false, reading.ReasonOrEmpty, null, null),
    };

    private static PsiLineReport PsiLine(Reading<PressureLine> reading) => reading switch
    {
        Reading<PressureLine>.Available { Value: var l } => new PsiLineReport(true, null, l.Avg10, l.Avg60, l.Avg300, l.TotalMicroseconds),
        _ => new PsiLineReport(false, reading.ReasonOrEmpty, null, null, null, null),
    };

    private static ProcessesReport Processes(Reading<ProcessSnapshot> reading) => reading switch
    {
        Reading<ProcessSnapshot>.Available { Value: var p } => new ProcessesReport(
            true, null, p.ProcessCount, p.KernelThreads, p.Vanished, p.ContainerProcesses, p.HeldBytesTotal,
            [.. p.Top.Select(Process)], [.. p.Families.Select(f => new FamilyReport(f.Name, f.Count, f.HeldBytes))], [.. p.MntWalkers.Select(Process)]),
        _ => new ProcessesReport(false, reading.ReasonOrEmpty, null, null, null, null, null, null, null, null),
    };

    private static ProcessReport Process(ProcessEntry e) =>
        new(
            e.Pid, e.ParentPid, e.User, e.Name, e.State.ToString(), e.RssAnonBytes, e.RssShmemBytes, e.HeldBytes,
            Number(e.Age.Map(a => Math.Round(a.TotalSeconds))), Number(e.CpuSeconds.Map(c => Math.Round(c, 2))), Text(e.Cwd),
            e.CommandLine, e.Family, e.Orphaned, e.HasTty, e.UnderMnt);

    private static ContainersReport Containers(Reading<ContainerSet> reading) => reading switch
    {
        Reading<ContainerSet>.Available { Value: var c } => new ContainersReport(
            true, null, c.Containers.Count, c.MemoryCurrentTotal, c.AnonShmemTotal,
            [.. c.Containers.Select(x => new ContainerReport(x.Id, Bytes(x.MemoryCurrentBytes), Bytes(x.AnonShmemBytes)))]),
        _ => new ContainersReport(false, reading.ReasonOrEmpty, null, null, null, null),
    };

    private static UnattributedReport Unattributed(Unattributed value) => value switch
    {
        Collectors.Unattributed.Remainder r => new UnattributedReport("remainder", r.Bytes, null, null),
        Collectors.Unattributed.InconsistentSample s => new UnattributedReport(
            "inconsistentSample", null, s.OvershootBytes, "the processes and containers held more than AnonPages + Shmem in this sample; it was read while memory moved"),
        Collectors.Unattributed.NotComputed n => new UnattributedReport("unavailable", null, null, n.Reason),
        _ => throw new System.Diagnostics.UnreachableException("Unattributed is a closed set"),
    };

    private static VolumeReport Volume(Reading<VolumeUsage> reading) => reading switch
    {
        Reading<VolumeUsage>.Available { Value: var v } => new VolumeReport(true, null, v.Path, v.TotalBytes, v.UsedBytes, v.AvailableBytes, v.UsedPercent),
        _ => new VolumeReport(false, reading.ReasonOrEmpty, null, null, null, null, null),
    };

    private static HostReport Host(Reading<HostSample> reading) => reading switch
    {
        Reading<HostSample>.Available { Value: var h } => new HostReport(
            true, null, HostMemory(h.Memory), Volume(h.SystemDrive), Bytes(h.VmmemWorkingSetBytes), Bytes(Reading.Missing<long>(VhdxNotCollected))),
        _ => new HostReport(false, reading.ReasonOrEmpty, null, null, null, null),
    };

    private static HostMemoryReport HostMemory(Reading<HostMemory> reading) => reading switch
    {
        Reading<HostMemory>.Available { Value: var m } => new HostMemoryReport(true, null, m.TotalBytes, m.AvailableBytes),
        _ => new HostMemoryReport(false, reading.ReasonOrEmpty, null, null),
    };

    private static SlowPartReport ContainerStats(Reading<AgedPart<ContainerStatsSample>> reading) => reading switch
    {
        Reading<AgedPart<ContainerStatsSample>>.Available { Value: var p } => new SlowPartReport(
            true, null, p.RunId.Text, p.SampledAt, Math.Round(p.Age.TotalSeconds), p.Value.Containers ?? [], null, null),
        _ => SlowUnavailable(reading.ReasonOrEmpty),
    };

    private static SlowPartReport WindowsClock(Reading<AgedPart<WindowsClockSample>> reading) => reading switch
    {
        Reading<AgedPart<WindowsClockSample>>.Available { Value: var p } => new SlowPartReport(
            true, null, p.RunId.Text, p.SampledAt, Math.Round(p.Age.TotalSeconds), null, p.Value.OffsetSeconds, p.Value.LaunchLatencySeconds),
        _ => SlowUnavailable(reading.ReasonOrEmpty),
    };

    private static SlowPartReport SlowUnavailable(string reason) => new(false, reason, null, null, null, null, null, null);
}
