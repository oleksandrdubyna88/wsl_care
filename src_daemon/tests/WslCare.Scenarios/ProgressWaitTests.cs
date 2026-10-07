using System.Diagnostics;

using FluentAssertions;

using WslCare.FakeTool;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// The harness's wait on progress (<see cref="ProgressWait"/>) is code under test (generated-code-tests rule §3): a child
/// that keeps showing progress outlives the silence window, a silent one is killed when the window passes — whatever cap
/// is left — and one that progresses for ever still ends at the cap; and a scenario's CLI run counts every fake call as
/// progress. The child is a fake scripted to sleep (its <c>DelayMilliseconds</c>), so the same tests run on every OS.
/// How a wait ENDS is in here too (the retro round over PR #15, 2026-10-07): a child killed at its silence or its cap is gone
/// when the <see cref="TimeoutException"/> arrives, and a child that exits while something it started still holds its output
/// ends in a timeout naming it within seconds, never at the cap.
/// </summary>
public sealed class ProgressWaitTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    /// <summary>Far longer than any wait below may take: the time it would take a child to be waited out instead of cut.</summary>
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(60);

    private static ScenarioHome Sleeper(string purpose, TimeSpan sleep)
    {
        var home = new ScenarioHome(purpose, ["docker"]);
        home.Answer(new FakeAnswer("docker", ["sleep"], 7, string.Empty, string.Empty, (int)sleep.TotalMilliseconds));
        return home;
    }

    private static Task<ChildResult> RunSleeper(ScenarioHome home, ProgressWait wait) =>
        ChildProcess.RunAsync(Path.Combine(home.FakeBin, FakeToolProtocol.FileName("docker", OperatingSystem.IsWindows())), ["sleep"], home.Environment, home.WorkingDirectory, progress: wait);

    /// <summary>A mark that changes on every read: a child that is always doing something.</summary>
    private static Func<long> Busy()
    {
        var reads = 0L;
        return () => Interlocked.Increment(ref reads);
    }

    [Fact]
    public async Task A_child_that_keeps_making_progress_runs_past_the_silence_window_to_its_own_exit()
    {
        using var home = Sleeper("progress-busy", TimeSpan.FromSeconds(4));

        var result = await RunSleeper(home, new ProgressWait(Busy(), Window, Long));

        result.Exit.Should().Be(7, "the fake slept four times the window and exited with its scripted code: progress kept it alive");
    }

    [Fact]
    public async Task A_silent_child_is_killed_once_the_silence_window_passes_long_before_its_cap()
    {
        using var home = Sleeper("progress-silent", Long);
        var watch = Stopwatch.StartNew();

        var run = () => RunSleeper(home, new ProgressWait(() => 0, Window, Long));

        (await run.Should().ThrowAsync<TimeoutException>()).WithMessage("* sleep made no progress for 1 s");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "the silence ended the wait, not the 60 s cap or the fake's own 60 s sleep");
    }

    [Fact]
    public async Task A_child_that_makes_progress_for_ever_still_ends_at_the_cap()
    {
        using var home = Sleeper("progress-cap", Long);
        var watch = Stopwatch.StartNew();

        var run = () => RunSleeper(home, new ProgressWait(Busy(), Window, TimeSpan.FromSeconds(3)));

        (await run.Should().ThrowAsync<TimeoutException>()).WithMessage("* sleep did not exit within 3 s");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "the cap ended the wait, not the fake's own 60 s sleep");
    }

    /// <summary>A mark that changes until the fake has recorded its call, then stays: the silence is counted from the moment the
    /// fake is surely running (and its pid recorded), whatever its start cost on a loaded machine.</summary>
    private static Func<long> BusyUntilCalled(ScenarioHome home)
    {
        var busy = Busy();
        return () => home.Calls.Count == 0 ? busy() : 0;
    }

    /// <summary>The kill was sent and the exception thrown at once: the scenario then disposed its temporary home while the
    /// killed fake could still be exiting in it, and nobody looked at the two output reads again. The cap is 15 s, so the fake
    /// has recorded its pid before it — a 3 s cap here killed a fake that had not started yet in a loaded Windows run.</summary>
    [Theory]
    [InlineData("silence")]
    [InlineData("cap")]
    public async Task A_child_killed_at_its_silence_or_its_cap_has_exited_when_the_timeout_arrives(string end)
    {
        using var home = Sleeper("progress-killed-" + end, Long);
        var wait = end == "silence" ? new ProgressWait(BusyUntilCalled(home), Window, Long) : new ProgressWait(Busy(), Window, TimeSpan.FromSeconds(15));

        var run = () => RunSleeper(home, wait);

        await run.Should().ThrowAsync<TimeoutException>();
        var pid = home.Calls.Should().ContainSingle().Which.ProcessId;
        StillRunning(pid).Should().BeFalse($"the killed fake (pid {pid}) is awaited before the wait gives up, so nothing of it is left in the scenario's home");
    }

    /// <summary>Whether a process with that id is still running (one that is gone, or has exited, is not).</summary>
    private static bool StillRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The child exits at once and leaves a background <c>sleep</c> holding its stdout: the read of the output waited
    /// for that sleep up to the cap and surfaced as an <see cref="OperationCanceledException"/> naming nothing.</summary>
    [Fact]
    public async Task A_child_that_exits_while_its_output_is_held_open_ends_in_a_timeout_naming_it_soon_after_its_exit()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a background process inheriting the pipe is spelled in POSIX sh here; the drain is the same code on both families");
        var watch = Stopwatch.StartNew();

        var run = () => ChildProcess.RunAsync("/bin/sh", ["-c", "sleep 20 & echo started"], new Dictionary<string, string?>(), ceiling: TimeSpan.FromSeconds(12));

        (await run.Should().ThrowAsync<TimeoutException>()).WithMessage("/bin/sh -c sleep 20 & echo started exited, but its output was still open * s later*");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(11), "the output is drained for a few seconds after the exit, not until the 12 s ceiling or the sleep's 20 s");
    }

    [Fact]
    public async Task A_ceiling_and_a_progress_wait_together_are_refused()
    {
        using var home = Sleeper("progress-both", TimeSpan.Zero);

        var run = () => ChildProcess.RunAsync(Path.Combine(home.FakeBin, FakeToolProtocol.FileName("docker", OperatingSystem.IsWindows())), ["sleep"], home.Environment, home.WorkingDirectory, Long, home.Wait);

        await run.Should().ThrowAsync<ArgumentException>().WithMessage("a child is waited on a ceiling OR on its progress, not both*");
    }

    [Fact]
    public async Task A_scenario_run_whose_cli_calls_no_fake_for_the_silence_window_is_killed_for_it()
    {
        // docker version hangs 20 s. The product cuts it at its own 10 s probe ceiling and goes on, so the run would finish well
        // inside a 30 s total — only the scenario's 1 s silence, through ScenarioHome.RunAsync, can end it.
        using var home = new ScenarioHome("progress-cli") { Silence = Window };
        home.Answer(new FakeAnswer("docker", Core.Docker.DockerCommands.Version.Arguments, 0, string.Empty, string.Empty, 20_000));
        var watch = Stopwatch.StartNew();

        var run = () => home.RunAsync("collect", "--json");

        (await run.Should().ThrowAsync<TimeoutException>()).WithMessage("* collect --json made no progress for 1 s");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "the scenario's own silence ended the run, not a 30 s total");
    }

    [Fact]
    public async Task A_scenarios_cli_run_counts_every_fake_call_as_progress_and_is_otherwise_waited_as_before()
    {
        using var home = Sleeper("progress-scenario", TimeSpan.Zero);
        var wait = home.Wait;
        var before = wait.Mark();

        var called = await RunSleeper(home, new ProgressWait(() => 0, Long, Long));

        called.Exit.Should().Be(7);
        home.Calls.Should().ContainSingle();
        wait.Mark().Should().BeGreaterThan(before, "a fake call grows the call log, and that is the mark the scenario's runs are waited on");
        wait.Silence.Should().Be(ChildProcess.DefaultCeiling, "a CLI that calls nothing is killed in the same 30 s a total gave it");
        wait.Cap.Should().Be(ProgressWait.DefaultCap);
    }
}
