using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Collect;
using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Health;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Processes;
using WslCare.Core.Records;
using WslCare.Core.Status;
using WslCare.Core.Thresholds;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Collect;

/// <summary>
/// E15.S10 end to end inside the process: a full run reads Defender's counters through the third script, measures them against the
/// ledger's newest line (the same process, the same start marker), appends its own line, records the section in its detail and judges
/// <c>host.defenderCpu</c>. The readings are the measured line of research/2026-10-16_defender_cpu_cost.md moved four hours on.
/// </summary>
public sealed class CollectDefenderTests : IDisposable
{
    private static readonly DateTimeOffset First = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
    private readonly TempRoot _root = new("collect-defender");
    private readonly LinuxHostPaths _paths;
    private readonly PhysicalFileSystem _files;

    public CollectDefenderTests()
    {
        _paths = new LinuxHostPaths(LinuxEnvironment.Sandboxed(_root.Path));
        _files = new PhysicalFileSystem(_paths);
    }

    public void Dispose() => _root.Dispose();

    /// <summary>The measured process at <paramref name="when"/> having used <paramref name="cpuSeconds"/> of CPU in all.</summary>
    private static string Line(DateTimeOffset when, double cpuSeconds, int pid = 6964, long start = 134_365_236_258_808_755) =>
        $"defender={(long)(cpuSeconds * DefenderCpu.TicksPerSecond)}|{when.ToFileTime()}|{pid}|{start}\n";

    private async Task<CollectResult> RunAsync(DateTimeOffset at, string defenderAnswer)
    {
        var clock = new FixedTimeProvider(at);
        var runner = new RecordingCommandRunner { Default = new CommandOutcome.FailedToStart("not installed in this test") }
            .Script(DefenderCpuCommands.DefenderCpu.Argv, 0, defenderAnswer);
        var context = new CollectContext(_paths, _files, runner, clock, new FakeProbe(_paths.Side, clock), ConfigLoader.Load(_paths, _files), 4242, RunTrigger.Timer);
        return await CollectRun.RunAsync(context, TestContext.Current.CancellationToken);
    }

    private DefenderLedgerRead Ledger() => new DefenderCpuLedger(_paths, _files).Read();

    private RunDetail Detail(CollectResult result) =>
        JsonSerializer.Deserialize(File.ReadAllBytes(RunDetailStore.Absolute(_paths, result.DetailFile)), WslCareJsonContext.Default.RunDetail)!;

    private DefenderReport Defender(CollectResult result)
    {
        var detail = Detail(result);
        detail.Defender.Should().NotBeNull("every full run's detail carries the Defender section, read or not");
        return detail.Defender!;
    }

    private Verdict DefenderVerdict(CollectResult result)
    {
        var verdicts = Detail(result).Thresholds;
        verdicts.Select(v => v.Id).Should().Contain("host.defenderCpu", "the full run judges Defender's CPU");
        return verdicts.Single(v => v.Id == "host.defenderCpu");
    }

    [Fact]
    public async Task The_first_full_run_records_a_baseline_and_judges_nothing_it_says_why()
    {
        var run = await RunAsync(First, Line(First, 99_351));

        run.Recording.Should().Be(Recording.Recorded, run.Reason);
        var defender = Defender(run);
        defender.Available.Should().BeTrue(defender.Reason, "the script read MsMpEng");
        defender.CoresAverage!.Available.Should().BeFalse();
        defender.CoresAverage.Reason.Should().Be(DefenderLedgerRead.NoBaseline);
        defender.LedgerNote.Should().BeNull("the reading is in the ledger");
        DefenderVerdict(run).Should().Match<Verdict>(v => v.Level == Level.Unknown && v.Reason == DefenderLedgerRead.NoBaseline);
        Ledger().Lines.Should().ContainSingle();
    }

    [Fact]
    public async Task The_second_full_run_measures_the_cores_between_the_two_reads_records_them_and_the_verdict_judges_them()
    {
        await RunAsync(First, Line(First, 99_351));

        var second = await RunAsync(First.AddHours(4), Line(First.AddHours(4), 99_351 + (0.76 * 4 * 3600)));

        var defender = Defender(second);
        defender.CoresAverage.Should().Match<NumberFigure>(f => f.Available && Math.Abs(f.Value!.Value - 0.76) < 1e-6);
        defender.CoveredSeconds!.Value.Should().Be(4 * 3600);
        defender.CoveredHours!.Value.Should().BeApproximately(4, 1e-9);
        defender.Extrapolated.Should().BeTrue();
        defender.CoreHoursPerWeek!.Value.Should().BeApproximately(0.76 * 168, 1e-4);
        DefenderVerdict(second).Should().Match<Verdict>(v => v.Level == Level.Ok && v.Value.StartsWith("0.76 cores", StringComparison.Ordinal));
        Ledger().Lines.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_sustained_core_and_a_half_warns_and_the_history_line_carries_the_warning()
    {
        await RunAsync(First, Line(First, 1_000));

        var second = await RunAsync(First.AddHours(4), Line(First.AddHours(4), 1_000 + (1.5 * 4 * 3600)));

        DefenderVerdict(second).Level.Should().Be(Level.Warn);
        RunHistory.Read(_paths, _files).Records.Last().Warnings!.Should().Contain(w => w.Id == "host.defenderCpu" && w.Level == "warn");
    }

    [Fact]
    public async Task A_counter_that_went_down_is_unknown_with_the_reason_never_a_negative_figure()
    {
        await RunAsync(First, Line(First, 5_000));

        var second = await RunAsync(First.AddHours(4), Line(First.AddHours(4), 100));

        DefenderVerdict(second).Should().Match<Verdict>(v => v.Level == Level.Unknown && v.Reason.Contains("counter", StringComparison.Ordinal));
        Defender(second).CoresAverage!.Available.Should().BeFalse();
    }

    /// <summary>The engine restarted with the SAME pid and a larger counter: a pid-only rule would report 3.4 cores and warn on a bogus figure.</summary>
    [Fact]
    public async Task A_restarted_engine_that_kept_its_pid_is_unknown_not_a_bogus_warning()
    {
        await RunAsync(First, Line(First, 1_000));

        var second = await RunAsync(First.AddHours(4), Line(First.AddHours(4), 50_000, start: 134_366_100_000_000_000));

        DefenderVerdict(second).Should().Match<Verdict>(v => v.Level == Level.Unknown && v.Reason == DefenderCpu.Restarted);
        Defender(second).CoresAverage!.Available.Should().BeFalse("no same-process span, so no cores");
    }

    [Fact]
    public async Task A_stalled_script_leaves_the_section_unavailable_with_its_reason_and_the_run_still_completes()
    {
        var run = await RunAsync(First, string.Empty);
        var stalled = new RecordingCommandRunner { Default = new CommandOutcome.FailedToStart("not installed in this test") }
            .Script(DefenderCpuCommands.DefenderCpu.Argv, new CommandOutcome.TimedOut(CapturedText.Empty, CapturedText.Empty, TimeSpan.FromSeconds(15)));
        var clock = new FixedTimeProvider(First.AddHours(4));
        var again = await CollectRun.RunAsync(new CollectContext(_paths, _files, stalled, clock, new FakeProbe(_paths.Side, clock), ConfigLoader.Load(_paths, _files), 4243, RunTrigger.Timer), TestContext.Current.CancellationToken);

        again.Recording.Should().Be(Recording.Recorded, again.Reason);
        var defender = Defender(again);
        defender.Available.Should().BeFalse();
        defender.Reason.Should().Contain("did not answer within 15 s; its process tree was killed");
        DefenderVerdict(again).Level.Should().Be(Level.Unknown);
        run.Recording.Should().Be(Recording.Recorded);
    }

    [Fact]
    public async Task The_detail_names_the_defender_section_on_disk()
    {
        var run = await RunAsync(First, Line(First, 99_351));

        File.ReadAllText(RunDetailStore.Absolute(_paths, run.DetailFile)).Should().Contain("\"defender\":").And.Contain("\"coresAverage\"");
    }
}
