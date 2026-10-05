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
