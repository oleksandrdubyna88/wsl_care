using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Records;
using WslCare.Core.Status;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Collectors;

/// <summary>The two probes and the <c>status --json</c> shape they feed (plan §6, §15b #5, #7).</summary>
public sealed class ProbeTests
{
    private const long KiB = 1024;

    private static ProbeSample FixtureSample()
    {
        var paths = ProcfsFixture.PathsAt(ProcfsFixture.Root);
        var files = ProcfsFixture.LinkOverlay(new PhysicalFileSystem(paths), ProcfsFixture.Root);
        return new LinuxProbe(files, paths, new FixedTimeProvider(ProcfsFixture.CapturedAt)).Sample(CancellationToken.None);
    }

    private static ConfigLoadResult Defaults()
    {
        using var host = new SandboxHost("cfg");
        return ConfigLoader.Load(host.Paths, host.Files);
    }

    [Fact]
    public void The_linux_probe_over_the_captured_tree_reads_memory_processes_containers_and_df()
    {
        var sample = FixtureSample();

        sample.Side.Should().Be(HostSide.Wsl);
        var vm = sample.Vm.Should().BeOfType<Reading<VmSample>.Available>().Subject.Value;
        var memory = vm.Memory.Should().BeOfType<Reading<MemorySnapshot>.Available>().Subject.Value;
        memory.Total.Should().Be(Reading.Of(47_066_772 * KiB));
        memory.PageCache.Should().Be(Reading.Of((8_022_288 + 2_241_336) * KiB), "Cached + Buffers");
        memory.SwapUsed.Should().Be(Reading.Of(0L), "SwapTotal equals SwapFree in the fixture: a measured zero");
        memory.AvailablePercent.Should().Be(Reading.Of(Math.Round(100.0 * 31_458_292 / 47_066_772, 1)));
        memory.Fragmentation.IsAvailable.Should().BeTrue();
        memory.Pressure.Cpu.IsAvailable.Should().BeTrue();
        vm.Containers.ValueOr(new ContainerSet([])).Containers.Should().HaveCount(10);
        vm.Processes.ValueOr(null!).ProcessCount.Should().Be(51);
        vm.Unattributed.Should().BeOfType<Unattributed.Remainder>();
        vm.RootVolume.Should().BeOfType<Reading<VolumeUsage>.Available>("df of the sandbox root measures the volume holding it");
        sample.Host.Should().BeOfType<Reading<HostSample>.Unavailable>().Which.Reason.Should().Be(LinuxProbe.HostIsTheOtherBinary);
    }

    [Fact]
    public void Over_an_empty_root_every_figure_is_unavailable_with_its_path_and_none_is_zero()
    {
        using var root = new TempRoot("empty-vm");
        var paths = ProcfsFixture.PathsAt(root.Path);

        var report = StatusReports.From(new LinuxProbe(new PhysicalFileSystem(paths), paths, new FixedTimeProvider()).Sample(CancellationToken.None), Slow(), Defaults());

        report.Vm.Available.Should().BeTrue("the side is this binary's; its parts say what they could not read");
        report.Vm.Memory!.Available.Should().BeFalse();
        report.Vm.Memory.Reason.Should().Contain("meminfo");
        report.Vm.Processes!.Available.Should().BeFalse();
        report.Vm.Containers!.Available.Should().BeFalse();
        report.Vm.Unattributed!.State.Should().Be("unavailable");
        var json = JsonSerializer.Serialize(report, WslCareJsonContext.Default.StatusReport);
        using var document = JsonDocument.Parse(json);
        var memory = document.RootElement.GetProperty("vm").GetProperty("memory");
        memory.GetProperty("available").GetBoolean().Should().BeFalse();
        memory.TryGetProperty("total", out _).Should().BeFalse("an unread section carries no figures at all");
        document.RootElement.GetProperty("vm").GetProperty("unattributed").TryGetProperty("bytes", out _).Should().BeFalse("never 0 in place of unknown");
    }

    [Fact]
    public void An_unavailable_figure_is_written_with_its_reason_and_without_a_value_key()
    {
        var json = JsonSerializer.Serialize(
            StatusReports.From(
                new ProbeSample(HostSide.Windows, FixedTimeProvider.DefaultNow, TimeSpan.FromMilliseconds(12), Reading.Missing<VmSample>(WindowsProbe.VmIsTheOtherBinary),
                    Reading.Of(new HostSample(Reading.Of(new HostMemory(100, 40)), Reading.Missing<VolumeUsage>("no drive"), Reading.Missing<long>("no vmmemWSL process: the WSL VM is not running")))),
                Slow(),
                Defaults()),
            WslCareJsonContext.Default.StatusReport);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        root.GetProperty("schemaVersion").GetInt32().Should().Be(SchemaVersion.Current);
        root.GetProperty("side").GetString().Should().Be("windows");
        root.GetProperty("sampleMilliseconds").GetInt64().Should().Be(12);
        root.GetProperty("vm").GetProperty("reason").GetString().Should().Be(WindowsProbe.VmIsTheOtherBinary);
        var vmmem = root.GetProperty("host").GetProperty("vmmemWorkingSet");
        vmmem.GetProperty("available").GetBoolean().Should().BeFalse();
        vmmem.GetProperty("reason").GetString().Should().Contain("not running");
        vmmem.TryGetProperty("bytes", out _).Should().BeFalse();
        root.GetProperty("host").GetProperty("vhdx").GetProperty("reason").GetString().Should().Be(StatusReports.VhdxNotCollected);
        root.GetProperty("host").GetProperty("memory").GetProperty("availableBytes").GetInt64().Should().Be(40);
    }

    [Fact]
    public void An_inconsistent_sample_is_written_as_a_state_with_the_overshoot_and_no_negative_number()
    {
        var vm = new VmSample(Reading.Missing<MemorySnapshot>("x"), Reading.Missing<ProcessSnapshot>("x"), Reading.Missing<ContainerSet>("x"), new Unattributed.InconsistentSample(5), Reading.Missing<VolumeUsage>("x"));
        var report = StatusReports.From(new ProbeSample(HostSide.Wsl, FixedTimeProvider.DefaultNow, TimeSpan.Zero, Reading.Of(vm), Reading.Missing<HostSample>("y")), Slow(), Defaults());

        report.Vm.Unattributed.Should().Be(new UnattributedReport("inconsistentSample", null, 5, report.Vm.Unattributed!.Reason));
        report.Vm.Unattributed.Reason.Should().Contain("more than AnonPages + Shmem");
    }

    [Fact]
    public void The_windows_probe_reports_host_ram_the_system_drive_and_vmmem_and_names_the_vm_as_the_other_binary()
    {
        using var root = new TempRoot("win");
        var paths = new WindowsHostPaths(WindowsEnvironment.Sandboxed(root.Path));
        var probe = new WindowsProbe(new PhysicalFileSystem(paths), paths, new FakeCounters(Reading.Of(new HostMemory(91L << 30, 5L << 30)), Reading.Of(21L << 30)), new FixedTimeProvider());

        var sample = probe.Sample(CancellationToken.None);

        sample.Side.Should().Be(HostSide.Windows);
        sample.Vm.Should().BeOfType<Reading<VmSample>.Unavailable>().Which.Reason.Should().Be(WindowsProbe.VmIsTheOtherBinary);
        var host = sample.Host.Should().BeOfType<Reading<HostSample>.Available>().Subject.Value;
        host.Memory.Should().Be(Reading.Of(new HostMemory(91L << 30, 5L << 30)));
        host.VmmemWorkingSetBytes.Should().Be(Reading.Of(21L << 30));
        host.SystemDrive.Should().BeOfType<Reading<VolumeUsage>.Available>().Which.Value.Path.Should().Be(paths.SystemDrive);
    }

    [Fact]
    public void The_real_windows_counters_read_this_hosts_ram_without_starting_anything()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "GlobalMemoryStatusEx and vmmemWSL exist on Windows only; the Linux legs prove the Linux probe");
        if (OperatingSystem.IsWindows())
        {
            var memory = new Win32Counters().Memory().Should().BeOfType<Reading<HostMemory>.Available>().Subject.Value;

            memory.TotalBytes.Should().BeGreaterThan(memory.AvailableBytes).And.BeGreaterThan(1L << 30);
        }
    }

    [Fact]
    public void A_volume_reports_df_figures_and_its_used_percent_excludes_the_reserve()
    {
        var usage = new VolumeUsage("/", TotalBytes: 1000, UsedBytes: 600, AvailableBytes: 300);

        usage.UsedPercent.Should().Be(66.7, "df: used / (used + avail); the 100 reserved bytes are neither");
    }

    private static LastSlowParts Slow() =>
        new(Reading.Missing<AgedPart<ContainerStatsSample>>(LastFullRun.NoFullRunYet), Reading.Missing<AgedPart<WindowsClockSample>>(LastFullRun.NoFullRunYet));

    private sealed class FakeCounters(Reading<HostMemory> memory, Reading<long> vmmem) : IWindowsCounters
    {
        public Reading<HostMemory> Memory() => memory;

        public Reading<long> VmmemWorkingSet() => vmmem;
    }
}
