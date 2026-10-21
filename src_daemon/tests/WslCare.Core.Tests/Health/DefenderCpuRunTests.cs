using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Files;
using WslCare.Core.Health;
using WslCare.Core.Hosting;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Health;

/// <summary>
/// E15.S10: one full run's step between the script's reading and the verdict — the ledger read, the two-point rule, the append, the
/// week. The readings are the measured line of research/2026-10-16_defender_cpu_cost.md moved forward in time with the process
/// (pid and start marker) kept — or not, for a restart.
/// </summary>
public sealed class DefenderCpuRunTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 8, 0, 0, TimeSpan.Zero);
    private readonly TempRoot _root = new("defender-run");
    private readonly LinuxHostPaths _paths;
    private readonly PhysicalFileSystem _files;

    public DefenderCpuRunTests()
    {
        _paths = new LinuxHostPaths(LinuxEnvironment.Sandboxed(_root.Path));
        _files = new PhysicalFileSystem(_paths);
    }

    public void Dispose() => _root.Dispose();

    /// <summary>The measured process: pid 6964, started at the measured instant, with 99 351 s of CPU at the measured instant.</summary>
    private static DefenderReading At(DateTimeOffset when, double cpuSeconds, int pid = 6964, long start = 134_365_236_258_808_755) =>
        new((long)(cpuSeconds * DefenderCpu.TicksPerSecond), when.ToFileTime(), pid, start);

    private static DefenderSample Sample(DateTimeOffset when, DefenderReading reading) =>
        new(when, Reading.Of(TimeSpan.FromMilliseconds(420)), Reading.Of(reading));

    private DefenderOutcome Run(DateTimeOffset when, DefenderReading reading, bool mayRecord = true) => DefenderCpuRun.Apply(_paths, _files, Sample(when, reading), mayRecord);

    private DefenderLedgerRead Ledger() => new DefenderCpuLedger(_paths, _files).Read();

    [Fact]
    public void The_first_run_records_a_baseline_and_has_no_usage_and_no_week()
    {
        var outcome = Run(Start, At(Start, 99_351));

        outcome.Usage.IsAvailable.Should().BeFalse();
        outcome.Usage.ReasonOrEmpty.Should().Be(DefenderLedgerRead.NoBaseline);
        outcome.Week.IsAvailable.Should().BeFalse();
        outcome.LedgerNote.Should().BeEmpty("the reading was recorded");
        var lines = Ledger().Lines;
        lines.Should().ContainSingle().Which.Should().Match<DefenderLedgerLine>(l => l.From == null && l.Cores == null && l.Pid == 6964);
    }

    [Fact]
    public void The_second_run_measures_the_cores_between_the_two_reads_and_records_the_span()
    {
        Run(Start, At(Start, 99_351));

        var outcome = Run(Start.AddHours(4), At(Start.AddHours(4), 99_351 + (0.76 * 4 * 3600)));

        var usage = outcome.Usage.Should().BeOfType<Reading<DefenderUsage>.Available>(outcome.Usage.ReasonOrEmpty).Subject.Value;
        usage.Cores.Should().BeApproximately(0.76, 1e-6);
        usage.Covered.Should().Be(TimeSpan.FromHours(4));
        var week = outcome.Week.Should().BeOfType<Reading<DefenderWeek>.Available>(outcome.Week.ReasonOrEmpty).Subject.Value;
        week.CoveredHours.Should().BeApproximately(4, 1e-9);
        week.Extrapolated.Should().BeTrue();
        week.CoreHoursPerWeek.Should().BeApproximately(0.76 * 168, 1e-6);
        Ledger().Lines.Should().HaveCount(2);
        Ledger().Lines[1].Should().Match<DefenderLedgerLine>(l => l.From == Start.ToFileTime() && Math.Abs(l.Cores!.Value - 0.76) < 1e-6);
    }

    [Fact]
    public void A_restart_between_two_runs_is_a_new_baseline_and_its_gap_adds_no_hours_to_the_week()
    {
        // Pid 6964 for 12 h; the engine restarts (an update) as pid 7040; 12 h more. Between the second and third run there is no span.
        Run(Start, At(Start, 1_000));
        Run(Start.AddHours(12), At(Start.AddHours(12), 1_000 + (0.5 * 12 * 3600)));
        var restarted = Run(Start.AddHours(48), At(Start.AddHours(48), 30, pid: 7040, start: 134_366_000_000_000_000));
        var last = Run(Start.AddHours(60), At(Start.AddHours(60), 30 + (1.0 * 12 * 3600), pid: 7040, start: 134_366_000_000_000_000));

        restarted.Usage.ReasonOrEmpty.Should().Be(DefenderCpu.Restarted, "a new pid AND a new start marker: nothing is invented for the 36 hours in between");
        var week = last.Week.Should().BeOfType<Reading<DefenderWeek>.Available>(last.Week.ReasonOrEmpty).Subject.Value;
        week.CoveredHours.Should().BeApproximately(24, 1e-9, "12 h and 12 h; the restart's gap adds none");
        week.CoreHoursPerWeek.Should().BeApproximately((6.0 + 12.0) / 24 * 168, 1e-6);
        Ledger().Lines.Select(l => l.Cores).Should().Equal([null, 0.5, null, 1.0]);
    }

    /// <summary>Windows reuses pids and an engine update restarts MsMpEng without a reboot: the new process may have the SAME pid and a LARGER
    /// counter than the old one had. Only its start marker tells them apart — the ledger records a baseline, not a span.</summary>
    [Fact]
    public void A_pid_reused_by_a_new_process_is_a_new_baseline_in_the_ledger_too_even_with_a_larger_counter()
    {
        Run(Start, At(Start, 1_000));

        var reused = Run(Start.AddHours(4), At(Start.AddHours(4), 90_000, pid: 6964, start: 134_366_100_000_000_000));

        reused.Usage.ReasonOrEmpty.Should().Be(DefenderCpu.Restarted);
        reused.Week.IsAvailable.Should().BeFalse("no same-process span exists");
        Ledger().Lines.Should().HaveCount(2);
        Ledger().Lines[1].Should().Match<DefenderLedgerLine>(l => l.From == null && l.Cores == null && l.Cpu == 90_000 * DefenderCpu.TicksPerSecond, "the new process is the baseline the next run measures against");
    }

    /// <summary>The counter of the SAME process went down (a restored snapshot, a counter reset): no negative span is invented, the reading becomes
    /// the baseline, and the run after it measures against it.</summary>
    [Fact]
    public void A_decreasing_counter_of_the_same_process_is_a_new_baseline_with_no_negative_span()
    {
        Run(Start, At(Start, 5_000));

        var down = Run(Start.AddHours(4), At(Start.AddHours(4), 100));
        var next = Run(Start.AddHours(8), At(Start.AddHours(8), 100 + (0.25 * 4 * 3600)));

        down.Usage.IsAvailable.Should().BeFalse();
        down.Usage.ReasonOrEmpty.Should().Contain("counter");
        down.Week.IsAvailable.Should().BeFalse("there is no span to sum");
        Ledger().Lines.Select(l => l.Cores).Should().Equal([null, null, 0.25]);
        next.Usage.Should().BeOfType<Reading<DefenderUsage>.Available>(next.Usage.ReasonOrEmpty).Which.Value.Cores.Should().BeApproximately(0.25, 1e-6);
    }

    [Fact]
    public void A_failed_read_records_nothing_and_leaves_the_baseline_for_the_next_run_to_span()
    {
        Run(Start, At(Start, 1_000));
        var failed = DefenderCpuRun.Apply(_paths, _files, DefenderSample.Unavailable(Start.AddHours(4), "powershell.exe (the Defender CPU script) did not answer within 15 s; its process tree was killed"), mayRecord: true);

        failed.Usage.ReasonOrEmpty.Should().Contain("did not answer within 15 s");
        Ledger().Lines.Should().ContainSingle("a read that did not answer is not a line");

        var next = Run(Start.AddHours(8), At(Start.AddHours(8), 1_000 + (2.0 * 8 * 3600)));

        next.Usage.Should().BeOfType<Reading<DefenderUsage>.Available>().Which.Value.Should().Match<DefenderUsage>(u => Math.Abs(u.Cores - 2.0) < 1e-6 && u.Covered == TimeSpan.FromHours(8), "the span runs from the last READ, across the missed run");
    }

    [Fact]
    public void A_failed_read_still_reports_the_week_the_ledger_already_holds()
    {
        Run(Start, At(Start, 1_000));
        Run(Start.AddHours(4), At(Start.AddHours(4), 1_000 + (1.0 * 4 * 3600)));

        var failed = DefenderCpuRun.Apply(_paths, _files, DefenderSample.Unavailable(Start.AddHours(8), "MsMpEng is not running"), mayRecord: true);

        failed.Usage.IsAvailable.Should().BeFalse();
        failed.Week.Should().BeOfType<Reading<DefenderWeek>.Available>().Which.Value.CoveredHours.Should().BeApproximately(4, 1e-9);
    }

    [Fact]
    public void A_run_that_may_not_record_measures_against_the_ledger_and_writes_nothing()
    {
        Run(Start, At(Start, 1_000));

        var outcome = Run(Start.AddHours(4), At(Start.AddHours(4), 1_000 + (0.5 * 4 * 3600)), mayRecord: false);

        outcome.Usage.Should().BeOfType<Reading<DefenderUsage>.Available>().Which.Value.Cores.Should().BeApproximately(0.5, 1e-6);
        outcome.LedgerNote.Should().NotBeEmpty("a read-only run says why the reading is not in the ledger");
        Ledger().Lines.Should().ContainSingle("only the first run's line");
    }

    [Fact]
    public void The_windows_binary_reads_and_writes_no_ledger()
    {
        var windows = new WindowsHostPaths(WindowsEnvironment.Sandboxed(_root.Path));

        var outcome = DefenderCpuRun.Apply(windows, _files, DefenderSample.Unavailable(Start, HealthCollector.DefenderCpuIsTheDistros), mayRecord: true);

        outcome.Usage.ReasonOrEmpty.Should().Be(HealthCollector.DefenderCpuIsTheDistros);
        outcome.Week.IsAvailable.Should().BeFalse();
        File.Exists(new DefenderCpuLedger(windows, _files).File).Should().BeFalse();
    }
}
