using System.Text;

using FluentAssertions;

using WslCare.Core.Collect;
using WslCare.Core.Collectors;
using WslCare.Core.Records;
using WslCare.Core.Status;
using WslCare.Core.Thresholds;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Status;

/// <summary>
/// E15.S2: <c>status --json</c>'s <c>disk</c> — the newest full run's disk section read from the SAME detail file its verdicts are
/// read from, carried with its basis (<c>fullRun</c>, the run, when it ended, how old it is); with no full run it says so, and the
/// Windows binary says the distro reads the virtual disks.
/// </summary>
public sealed class StatusDiskTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private readonly SandboxHost _sandbox = new("status-disk");

    public void Dispose() => _sandbox.Dispose();

    private static DiskReport Report() =>
        new(true, null, Now.AddHours(-2), new TextFigure(true, "Ubuntu", null), [], []);

    private RunId FullRun(DateTimeOffset startedAt, string disk)
    {
        var id = RunId.New(startedAt, 3);
        var json = $$"""
            {"schemaVersion":1,"runId":"{{id.Text}}","trigger":"timer","startedAt":"{{startedAt:O}}","endedAt":"{{startedAt.AddSeconds(40):O}}",
             "thresholds":[]{{disk}}}
            """;
        RunDetailStore.Write(_sandbox.Paths, _sandbox.Files, id, Encoding.UTF8.GetBytes(json));
        new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(
            new RunRecord(SchemaVersion.Current, id, RunTrigger.Timer, startedAt, startedAt.AddSeconds(40), RunOutcome.Completed, [], RunKind.Collect)
            {
                Slow = new SlowParts(),
                Detail = RunDetailStore.RelativePath(id),
            });
        return id;
    }

    private Reading<RecordedVerdicts> Read() => FullRunVerdicts.Read(_sandbox.Paths, _sandbox.Files, RunHistory.Read(_sandbox.Paths, _sandbox.Files));

    [Fact]
    public void A_disk_section_that_does_not_parse_costs_only_the_disk_section_never_the_carried_verdicts()
    {
        FullRun(Now.AddHours(-1), ""","disk":{"available":"yes","vhdx":7}""");

        var recorded = Read();

        recorded.IsAvailable.Should().BeTrue("the verdicts of the run are still carried when only its disk section is broken (own review)");
        var disk = ((Reading<RecordedVerdicts>.Available)recorded).Value.Disk;
        disk.IsAvailable.Should().BeFalse();
        disk.ReasonOrEmpty.Should().Contain("disk section").And.Contain("does not parse");
    }

    [Fact]
    public void Status_carries_the_full_runs_disk_section_with_its_basis_and_age()
    {
        var runId = RunId.New(Now.AddHours(-3), 4242);
        var endedAt = Now.AddHours(-3).AddSeconds(40);
        var recorded = Reading.Of(new RecordedVerdicts(runId, endedAt, []) { Disk = Reading.Of(Report()) });

        var disk = StatusDisk.From(recorded, Now);

        disk.Available.Should().BeTrue(disk.Reason);
        disk.DistroName!.Value.Should().Be("Ubuntu");
        disk.Basis.Should().Be(new VerdictBasis(VerdictSource.FullRun, runId.Text, endedAt, (3 * 3600) - 40));
    }

    [Fact]
    public void With_no_full_run_status_says_so()
    {
        var disk = StatusDisk.From(Reading.Missing<RecordedVerdicts>(LastFullRun.NoFullRunYet), Now);

        disk.Available.Should().BeFalse();
        disk.Reason.Should().Be(LastFullRun.NoFullRunYet);
        disk.Basis.Should().Be(new VerdictBasis(VerdictSource.FullRun, null, null, null));
    }

    [Fact]
    public void The_windows_binary_says_the_distro_reads_the_virtual_disks()
    {
        StatusDisk.OnWindows.Available.Should().BeFalse();
        StatusDisk.OnWindows.Reason.Should().Contain("the distro's full run").And.Contain("status");
    }

    [Fact]
    public void The_disk_section_is_read_from_the_same_detail_as_the_verdicts()
    {
        var id = FullRun(Now.AddHours(-1), ""","disk":{"available":true,"sampledAt":"2026-10-11T10:59:00+00:00","distroName":{"available":true,"value":"Ubuntu"},"vhdx":[],"volumes":[]}""");

        var recorded = Read().Should().BeOfType<Reading<RecordedVerdicts>.Available>().Subject.Value;

        recorded.RunId.Should().Be(id);
        var disk = recorded.Disk.Should().BeOfType<Reading<DiskReport>.Available>(recorded.Disk.ReasonOrEmpty).Subject.Value;
        disk.DistroName!.Value.Should().Be("Ubuntu");
    }

    [Fact]
    public void A_detail_written_before_the_disk_section_existed_says_so_and_names_the_run()
    {
        var id = FullRun(Now.AddHours(-1), string.Empty);

        var recorded = Read().Should().BeOfType<Reading<RecordedVerdicts>.Available>().Subject.Value;
        var disk = StatusDisk.From(Reading.Of(recorded), Now);

        disk.Available.Should().BeFalse();
        disk.Reason.Should().Contain(id.Text).And.Contain("no disk section");
        disk.Basis!.RunId.Should().Be(id.Text);
    }
}
