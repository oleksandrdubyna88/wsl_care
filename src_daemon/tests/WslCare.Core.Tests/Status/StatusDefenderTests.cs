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
/// E15.S10: <c>status --json</c>'s <c>defender</c> — Windows Defender's CPU exactly as the newest full run recorded it in its detail,
/// read from the SAME detail file its verdicts are read from, carried with its basis (<c>fullRun</c>, the run, when it ended, how old it
/// is); with no full run it says so, and the Windows binary says the distro reads it.
/// </summary>
public sealed class StatusDefenderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private readonly SandboxHost _sandbox = new("status-defender");

    public void Dispose() => _sandbox.Dispose();

    private const string Section = """
        ,"defender":{"available":true,"sampledAt":"2026-10-20T09:00:00+00:00","elapsedSeconds":{"available":true,"value":0.42},
         "coresAverage":{"available":true,"value":0.76},"coveredSeconds":{"available":true,"value":14400},
         "coreHoursPerWeek":{"available":true,"value":127.7},"coveredHours":{"available":true,"value":24},"extrapolated":true}
        """;

    private RunId FullRun(DateTimeOffset startedAt, string defender)
    {
        var id = RunId.New(startedAt, 3);
        var json = $$"""
            {"schemaVersion":1,"runId":"{{id.Text}}","trigger":"timer","startedAt":"{{startedAt:O}}","endedAt":"{{startedAt.AddSeconds(40):O}}",
             "thresholds":[]{{defender}}}
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
    public void Status_carries_the_full_runs_defender_section_with_its_run_and_age()
    {
        var id = FullRun(Now.AddHours(-3), Section);

        var defender = StatusDefender.From(Read(), Now);

        defender.Available.Should().BeTrue(defender.Reason);
        defender.CoresAverage.Should().Be(new NumberFigure(true, 0.76, null));
        defender.CoreHoursPerWeek!.Value.Should().Be(127.7);
        defender.CoveredHours!.Value.Should().Be(24);
        defender.Extrapolated.Should().BeTrue();
        defender.Basis.Should().Be(new VerdictBasis(VerdictSource.FullRun, id.Text, Now.AddHours(-3).AddSeconds(40), (3 * 3600) - 40));
    }

    [Fact]
    public void The_defender_section_is_read_from_the_same_detail_as_the_verdicts()
    {
        var id = FullRun(Now.AddHours(-1), Section);

        var recorded = Read().Should().BeOfType<Reading<RecordedVerdicts>.Available>().Subject.Value;

        recorded.RunId.Should().Be(id);
        recorded.Defender.Should().BeOfType<Reading<DefenderReport>.Available>(recorded.Defender.ReasonOrEmpty).Which.Value.CoresAverage!.Value.Should().Be(0.76);
    }

    [Fact]
    public void A_run_whose_script_did_not_answer_carries_its_reason_with_the_basis()
    {
        var id = FullRun(Now.AddHours(-1), ""","defender":{"available":false,"reason":"powershell.exe (the Defender CPU script) did not answer within 15 s; its process tree was killed"}""");

        var defender = StatusDefender.From(Read(), Now);

        defender.Available.Should().BeFalse();
        defender.Reason.Should().Contain("did not answer within 15 s");
        defender.CoresAverage.Should().BeNull("an unread figure is absent, never 0");
        defender.Basis!.RunId.Should().Be(id.Text);
    }

    [Fact]
    public void A_defender_section_that_does_not_parse_costs_only_the_defender_section_never_the_carried_verdicts()
    {
        FullRun(Now.AddHours(-1), ""","defender":{"available":"yes","coresAverage":7}""");

        var recorded = Read();

        recorded.IsAvailable.Should().BeTrue("the verdicts of the run are still carried when only its defender section is broken");
        var defender = ((Reading<RecordedVerdicts>.Available)recorded).Value.Defender;
        defender.IsAvailable.Should().BeFalse();
        defender.ReasonOrEmpty.Should().Contain("defender section").And.Contain("does not parse");
    }

    [Fact]
    public void A_detail_written_before_the_defender_section_existed_says_so_and_names_the_run()
    {
        var id = FullRun(Now.AddHours(-1), string.Empty);

        var defender = StatusDefender.From(Read(), Now);

        defender.Available.Should().BeFalse();
        defender.Reason.Should().Contain(id.Text).And.Contain("no defender section");
        defender.Basis!.RunId.Should().Be(id.Text);
    }

    [Fact]
    public void With_no_full_run_status_says_so()
    {
        var defender = StatusDefender.From(Reading.Missing<RecordedVerdicts>(LastFullRun.NoFullRunYet), Now);

        defender.Available.Should().BeFalse();
        defender.Reason.Should().Be(LastFullRun.NoFullRunYet);
        defender.Basis.Should().Be(new VerdictBasis(VerdictSource.FullRun, null, null, null));
    }

    [Fact]
    public void The_windows_binary_says_the_distro_reads_it()
    {
        StatusDefender.OnWindows.Available.Should().BeFalse();
        StatusDefender.OnWindows.Reason.Should().Contain("the distro's full run").And.Contain("status");
    }
}
