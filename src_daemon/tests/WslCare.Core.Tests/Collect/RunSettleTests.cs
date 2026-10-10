using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Actions.Engine;
using WslCare.Core.Collect;
using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Processes;
using WslCare.Core.Records;
using WslCare.Core.Thresholds;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Collect;

/// <summary>
/// PLAN_boot_settle.md: a timer run first waits until the machine has been up <c>timer.bootDelayMinutes</c> (2026-10-10: the
/// catch-up ran 07:44–07:48Z while Docker started 12 containers), then — at any time — while S6 says the machine is busy, in
/// <c>timer.busyCheckSeconds</c> steps, at most <c>timer.busyWaitMinutes</c>; then it runs anyway. An unread uptime or busy
/// signal is no wait. Every wait is the injected one: nothing here sleeps.
/// </summary>
public sealed class RunSettleTests
{
    private static readonly SettleSettings Defaults = new(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(20), TimeSpan.FromSeconds(60));

    private static readonly BusyJudgement Calm = new(BusyState.Calm, [], []);

    private static readonly BusyJudgement Busy = new(BusyState.Busy, [new BusyReason("cpu", "avg60", 40, 25, "thresholds.cpuPressureWarnPercent")], []);

    private static readonly BusyJudgement CannotSay = new(BusyState.Unknown, [], ["cpu: /proc/pressure/cpu could not be read"]);

    /// <summary>A probe whose uptime is fixed and whose busy answers come in order (the last one repeats); the waits it was asked for.</summary>
    private sealed class Script(Reading<TimeSpan> uptime, params BusyJudgement[] busy)
    {
        private int _asked;

        public List<TimeSpan> Waits { get; } = [];

        public SettleProbe Probe => new(() => uptime, () => busy[Math.Min(_asked++, busy.Length - 1)]);

        public Task Wait(TimeSpan delay, CancellationToken token)
        {
            Waits.Add(delay);
            return Task.CompletedTask;
        }

        public int Asked => _asked;
    }

    private static Task<RunSettled> Settle(Script script, SettleSettings? settings = null) =>
        RunSettle.WaitAsync(script.Probe, settings ?? Defaults, script.Wait, CancellationToken.None);

    [Fact]
    public async Task A_timer_run_within_the_boot_delay_waits_until_the_machine_has_been_up_that_long()
    {
        var script = new Script(Reading.Of(TimeSpan.FromSeconds(120)), Calm);

        var settled = await Settle(script);

        script.Waits.Should().Equal(TimeSpan.FromSeconds(780));
        settled.BootWait.Should().Be(TimeSpan.FromSeconds(780));
        settled.BusyWait.Should().Be(TimeSpan.Zero);
        settled.BusyAtEnd.Should().BeFalse();
    }

    [Fact]
    public async Task A_timer_run_after_the_boot_delay_does_not_wait()
    {
        var script = new Script(Reading.Of(TimeSpan.FromHours(3)), Calm);

        var settled = await Settle(script);

        script.Waits.Should().BeEmpty();
        settled.BootWait.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task A_busy_machine_holds_the_run_in_steps_up_to_the_busy_wait_then_it_runs_anyway()
    {
        var script = new Script(Reading.Of(TimeSpan.FromHours(3)), Busy);

        var settled = await Settle(script, Defaults with { BusyWait = TimeSpan.FromSeconds(150) });

        script.Waits.Should().Equal(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30));
        settled.BusyWait.Should().Be(TimeSpan.FromSeconds(150));
        settled.BusyAtEnd.Should().BeTrue("still busy when the bound ran out — the run goes anyway, and the record says so");
    }

    [Fact]
    public async Task A_machine_that_calms_down_ends_the_busy_wait_at_once()
    {
        var script = new Script(Reading.Of(TimeSpan.FromHours(3)), Busy, Busy, Calm);

        var settled = await Settle(script);

        script.Waits.Should().Equal(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        settled.BusyWait.Should().Be(TimeSpan.FromSeconds(120));
        settled.BusyAtEnd.Should().BeFalse();
    }

    [Fact]
    public async Task An_unread_busy_signal_or_uptime_is_no_wait()
    {
        var script = new Script(Reading.Missing<TimeSpan>("/proc/uptime could not be read"), CannotSay);

        var settled = await Settle(script);

        script.Waits.Should().BeEmpty("an unread signal is never read as busy, and an unread uptime never as a fresh boot");
        settled.Notes.Should().Contain(n => n.Contains("/proc/uptime could not be read", StringComparison.Ordinal))
            .And.Contain(n => n.Contains("cannot say", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Zero_turns_each_wait_off()
    {
        var script = new Script(Reading.Of(TimeSpan.FromSeconds(10)), Busy);

        var settled = await Settle(script, new SettleSettings(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(60)));

        script.Waits.Should().BeEmpty();
        settled.BootWait.Should().Be(TimeSpan.Zero);
        settled.BusyWait.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void The_settings_come_from_the_keys()
    {
        var config = ConfigLoader.Load([(ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults()))]).Config;

        SettleSettings.From(config).Should().Be(Defaults, "the embedded defaults: 15 min after boot, 20 min while busy, checked every 60 s");
    }
}

/// <summary>The settle step inside a real <c>collect --timer</c> run (Linux legs): it waits BEFORE the run lock and before
/// <c>running.json</c>, a run from a terminal never waits, and the record says how long it waited.</summary>
public sealed class RunSettleCollectTests : IDisposable
{
    private const int Pid = 4242;

    private readonly SandboxHost _sandbox = new("run-settle");
    private readonly FixedTimeProvider _clock = new();

    public void Dispose() => _sandbox.Dispose();

    private CollectContext Context(RunTrigger trigger, Func<TimeSpan, CancellationToken, Task> wait) =>
        new(_sandbox.Paths, _sandbox.Files, new RecordingCommandRunner { Default = new CommandOutcome.FailedToStart("not installed in this test") }, _clock,
            new FakeProbe(_sandbox.Paths.Side, _clock), ConfigLoader.Load(_sandbox.Paths, _sandbox.Files), Pid, trigger)
        {
            Processes = new FakeProcessTable().Alive(Pid, FixedTimeProvider.DefaultNow.AddMinutes(-1)),
            Wait = wait,
        };

    private void UptimeIs(double seconds)
    {
        var proc = ((LinuxHostPaths)_sandbox.Paths).ProcRoot;
        Directory.CreateDirectory(proc);
        File.WriteAllText(Path.Combine(proc, "uptime"), FormattableString.Invariant($"{seconds:0.00} 100.00\n"));
    }

    private RunDetail Detail(CollectResult result) =>
        JsonSerializer.Deserialize(File.ReadAllBytes(RunDetailStore.Absolute(_sandbox.Paths, result.DetailFile)), WslCareJsonContext.Default.RunDetail)!;

    [Fact]
    public async Task The_wait_holds_no_lock_and_writes_no_running_state_and_the_record_says_how_long_it_waited()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the settle step reads the distro's /proc; the Windows binary runs no timer");
        UptimeIs(120);
        var heldDuringWait = true;
        var runningDuringWait = true;
        var waits = new List<TimeSpan>();

        var result = await CollectRun.RunAsync(Context(RunTrigger.Timer, (delay, _) =>
        {
            waits.Add(delay);
            heldDuringWait = RunLock.TryTake(_sandbox.Paths, _sandbox.Files) is ExclusiveLock.Busy;
            runningDuringWait = File.Exists(RunningState.File(_sandbox.Paths));
            return Task.CompletedTask;
        }), CancellationToken.None);

        waits.Should().Contain(TimeSpan.FromSeconds(780), "up 2 minutes of the 15");
        heldDuringWait.Should().BeFalse("the settle step waits before the run lock is taken");
        runningDuringWait.Should().BeFalse("and before running.json is written");
        result.Recording.Should().Be(Recording.Recorded, result.Reason);
        Detail(result).Settled.Should().NotBeNull().And.Match<RunSettledReport>(s => s.BootWaitSeconds == 780 && s.BusyWaitSeconds == 0 && !s.BusyAtEnd);
    }

    [Fact]
    public async Task A_manual_collect_never_waits()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the settle step reads the distro's /proc; the Windows binary runs no timer");
        UptimeIs(120);
        var waits = new List<TimeSpan>();

        var result = await CollectRun.RunAsync(Context(RunTrigger.Cli, (delay, _) =>
        {
            waits.Add(delay);
            return Task.CompletedTask;
        }), CancellationToken.None);

        waits.Should().NotContain(TimeSpan.FromSeconds(780), "a run from a terminal or the panel never waits for the boot");
        Detail(result).Settled.Should().BeNull("only a timer run settles, and only it says so");
    }
}
