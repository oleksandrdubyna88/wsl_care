using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Collect;
using WslCare.Core.Health;
using WslCare.Core.Json;
using WslCare.Core.Status;
using WslCare.Core.Thresholds;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// E15.S2 end to end over the BUILT CLI: the full run turns the Windows disk script's answer (the 2026-10-10 capture,
/// <see cref="WindowsDiskFixture"/>) into the virtual disks' facts and three verdicts, its detail records them, and <c>status</c>
/// carries them with the run and its age. This distro's own disk is found by its NAME: a root <c>collect</c> started through
/// <c>wsl.exe</c> (the panel's <i>Run full check now</i>) carries <c>WSL_DISTRO_NAME</c> and records it; the next run — a systemd
/// unit, which has none — reads it back. On the Windows binary no script runs and both answers say the distro reads the disks.
/// </summary>
public sealed class VirtualDiskFlows
{
    private static CollectReport Report(ChildResult result) =>
        JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.CollectReport) ?? throw new InvalidOperationException($"collect printed no report: {result.Stderr}");

    private static StatusReport Status(ChildResult result) =>
        JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.StatusReport) ?? throw new InvalidOperationException($"status printed no report: {result.Stderr}");

    private static VhdxReport Vhdx(DiskReport disk, string name) => disk.Vhdx!.Single(v => v.Name == name);

    private static bool OnTheDistro(ScenarioHome home) => home.Paths.Side == Core.Hosting.HostSide.Wsl;

    [Fact]
    public async Task A_root_collect_through_wsl_exe_records_the_distro_name_and_the_next_run_without_it_maps_this_distros_disk_by_the_file()
    {
        using var home = CollectFlows.Captured("collect-virtual-disks");
        home.ClaimsRoot = true;
        home.Extra[DistroIdentity.Variable] = "Ubuntu";

        var first = await home.RunAsync("collect", "--json");

        first.Exit.Should().Be((int)ExitCode.Ok, first.Stderr);
        var disk = Report(first).Detail!.Disk;
        disk.Should().NotBeNull("every full run's detail carries the disk section, read or not");
        if (!OnTheDistro(home))
        {
            disk!.Available.Should().BeFalse();
            disk.Reason.Should().Be(HealthCollector.WindowsDiskIsTheDistros);
            File.Exists(DistroIdentity.File(home.Paths)).Should().BeFalse("only the distro's root run records the name");
            Status(await home.RunAsync("status", "--json")).Disk!.Reason.Should().Be(StatusDisk.ReadByTheDistro);
            return;
        }

        disk!.Available.Should().BeTrue(disk.Reason);
        disk.DistroName!.Value.Should().Be("Ubuntu");
        Vhdx(disk, "Ubuntu").MappedBy.Should().Be("mount");
        Vhdx(disk, "Ubuntu").Gap.Available.Should().BeTrue("file − its own / (statvfs)");
        Vhdx(disk, WindowsDiskParsers.DockerDataDiskName).MappedBy.Should().Be("dockerDf");
        Vhdx(disk, WindowsDiskParsers.DockerDataDiskName).Note.Should().Contain("estimate");
        Vhdx(disk, "Ubuntu-26.04").MappedBy.Should().Be("none");
        Vhdx(disk, "Ubuntu").GrowthBytesPerDay.Reason.Should().Contain("two measured sizes", "one measurement gives no growth");
        disk.Volumes!.Should().ContainSingle().Which.Letter.Should().Be("C");
        File.Exists(DistroIdentity.File(home.Paths)).Should().BeTrue("a root run started through wsl.exe records this distro's name");
        var thresholds = Report(first).Detail!.Thresholds;
        thresholds.Single(t => t.Id == DiskRules.GrowthId).Level.Should().Be(Level.Unknown);
        thresholds.Single(t => t.Id == DiskRules.HostFreeId).Level.Should().Be(Level.Unknown, "three files' possible growth is not known, so free space is never ok");

        home.Extra.Remove(DistroIdentity.Variable);
        var second = await home.RunAsync("collect", "--json");

        second.Exit.Should().Be((int)ExitCode.Ok, second.Stderr);
        var again = Report(second).Detail!;
        Vhdx(again.Disk!, "Ubuntu").MappedBy.Should().Be("mount", "the unit's run has no WSL_DISTRO_NAME: the recorded name maps the disk");
        Vhdx(again.Disk!, "Ubuntu").GrowthBytesPerDay.Reason.Should().Contain("too close to the previous measurement", "two runs seconds apart are below disk.growthMinHours");

        var status = Status(await home.RunAsync("status", "--json"));

        status.Disk!.Available.Should().BeTrue(status.Disk.Reason);
        status.Disk.Basis!.Source.Should().Be(VerdictSource.FullRun);
        status.Disk.Basis.RunId.Should().Be(again.RunId.Text, "the newest full run's section, with its run");
        status.Disk.Basis.AgeSeconds.Should().NotBeNull();
        status.Verdicts!.Single(v => v.Id == DiskRules.GapId).Basis!.Source.Should().Be(VerdictSource.FullRun);
    }

    [Fact]
    public async Task Without_a_recorded_name_no_distro_is_mapped_and_the_reason_says_how_the_name_becomes_known()
    {
        using var home = CollectFlows.Captured("collect-virtual-disks-unnamed");

        var result = await home.RunAsync("collect", "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        var disk = Report(result).Detail!.Disk!;
        if (!OnTheDistro(home))
        {
            disk.Available.Should().BeFalse();
            return;
        }

        disk.DistroName!.Available.Should().BeFalse();
        disk.DistroName.Reason.Should().Be(DistroIdentity.NotKnownYet);
        disk.Vhdx!.Where(v => v.Name != WindowsDiskParsers.DockerDataDiskName).Should().OnlyContain(v => v.MappedBy == "none" && v.Note == DistroIdentity.NotKnownYet);
        File.Exists(DistroIdentity.File(home.Paths)).Should().BeFalse("a run without the variable records nothing");
        Report(result).Detail!.Thresholds.Single(t => t.Id == DiskRules.GapId).Level.Should().NotBe(Level.Ok, "Docker's estimate alone never makes the gap ok while this distro's own disk is not judged (own review); the capture's Docker gap of about 34 GiB warns");
    }

    [Fact]
    public async Task A_collect_that_is_not_root_maps_by_its_environment_but_records_nothing()
    {
        using var home = CollectFlows.Captured("collect-virtual-disks-unprivileged");
        home.ClaimsRoot = false;
        home.Extra[DistroIdentity.Variable] = "Ubuntu";

        var result = await home.RunAsync("collect", "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        File.Exists(DistroIdentity.File(home.Paths)).Should().BeFalse("only a ROOT run writes the state folder's distro-name.json (own review)");
        if (OnTheDistro(home))
        {
            Vhdx(Report(result).Detail!.Disk!, "Ubuntu").MappedBy.Should().Be("mount", "the run's own environment still names the distro");
        }
    }
}
