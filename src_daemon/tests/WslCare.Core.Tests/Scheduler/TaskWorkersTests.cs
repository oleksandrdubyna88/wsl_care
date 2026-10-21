using FluentAssertions;

using WslCare.Core.Actions.Engine;
using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Processes;
using WslCare.Core.Scheduler;

namespace WslCare.Core.Tests.Scheduler;

/// <summary>WR-D3 (PLAN_windows_runner.md §3.2, §3.3 point 2): the closed table <c>tasks run</c> starts its worker from — this
/// binary's own path, the arguments spelt here, the deadline from its key; nothing else can be started.</summary>
public sealed class TaskWorkersTests
{
    /// <summary>An absolute path on the OS running the test (the table only asks that it be absolute; the verb is the Windows
    /// binary's, and the Linux legs still read the table).</summary>
    private static readonly string Self = OperatingSystem.IsWindows() ? "C:\\Users\\user\\AppData\\Local\\wsl-care\\bin\\0123\\wsl-care.exe" : "/home/user/wsl-care/bin/0123/wsl-care.exe";

    private static readonly EffectiveConfig Defaults = ConfigLoader.Load([(ConfigLoader.DefaultsFile, new WslCare.Core.Files.FileReadResult.Content(ConfigLoader.EmbeddedDefaults()))]).Config;

    /// <summary>The watch never takes the archive body's dry-run decision (it writes the first-week stamp): asking for it is a failure.</summary>
    private static DryRunDecision NeverAsked() => throw new InvalidOperationException("the watch asked for the archive body's dry-run decision");

    private static Func<DryRunDecision> Deciding(bool dry) => () => new DryRunDecision(dry, "a test says so");

    [Theory]
    [InlineData(false, new[] { "watch", "--timer" })]
    [InlineData(true, new[] { "watch", "--timer", "--json" })]
    public void The_watch_worker_is_this_binary_s_own_watch_timer_bounded_by_the_watch_s_run_limit(bool json, string[] arguments)
    {
        var launch = TaskWorkers.For(WindowsTask.Watch, Self, json, Defaults, NeverAsked).Should().BeOfType<Reading<WorkerLaunch>.Available>().Subject.Value;

        launch.Executable.Should().Be(Self);
        launch.Arguments.Should().Equal(arguments);
        launch.Deadline.Should().Be(TimeSpan.FromMinutes(Defaults.Int(ConfigKeys.McpWatchdog.RunLimitMinutes)));
        launch.DeadlineKey.Should().Be(ConfigKeys.McpWatchdog.RunLimitMinutes.Name, "the deadline and its key come from ONE table entry");
        TaskWorkers.DeadlineKey(WindowsTask.Watch).Should().Be(ConfigKeys.McpWatchdog.RunLimitMinutes);
    }

    [Theory]
    [InlineData(true, false, new[] { "archive", "preview" })]
    [InlineData(true, true, new[] { "archive", "preview", "--json" })]
    [InlineData(false, false, new[] { "archive", "run" })]
    [InlineData(false, true, new[] { "archive", "run", "--json" })]
    public void The_archive_worker_is_the_preview_while_dry_and_the_run_when_real_bounded_by_the_timer_s_run_limit(bool dry, bool json, string[] arguments)
    {
        var launch = TaskWorkers.For(WindowsTask.Archive, Self, json, Defaults, Deciding(dry)).Should().BeOfType<Reading<WorkerLaunch>.Available>().Subject.Value;

        launch.Executable.Should().Be(Self);
        launch.Arguments.Should().Equal(arguments);
        launch.Deadline.Should().Be(TimeSpan.FromMinutes(Defaults.Int(ConfigKeys.Timer.RunLimitMinutes)));
        launch.DeadlineKey.Should().Be(ConfigKeys.Timer.RunLimitMinutes.Name);
        TaskWorkers.DeadlineKey(WindowsTask.Archive).Should().Be(ConfigKeys.Timer.RunLimitMinutes);
    }

    /// <summary>WR-T2 (I2): the cleanup worker is exactly <c>&lt;self&gt; act A23 --timer --confirm [--json]</c> - the timer mark so A23's own dry
    /// week and its auto switch gate the run, <c>--confirm</c> because the parser refuses an <c>act</c> without a mode - bounded by the timer's
    /// run limit. The id in the table is the action's own.</summary>
    [Theory]
    [InlineData(false, new[] { "act", "A23", "--timer", "--confirm" })]
    [InlineData(true, new[] { "act", "A23", "--timer", "--confirm", "--json" })]
    public void The_cleanup_worker_is_this_binary_s_own_act_a23_timer_confirm_bounded_by_the_timer_s_run_limit(bool json, string[] arguments)
    {
        var launch = TaskWorkers.For(WindowsTask.Cleanup, Self, json, Defaults, NeverAsked).Should().BeOfType<Reading<WorkerLaunch>.Available>().Subject.Value;

        launch.Executable.Should().Be(Self);
        launch.Arguments.Should().Equal(arguments);
        launch.Deadline.Should().Be(TimeSpan.FromMinutes(Defaults.Int(ConfigKeys.Timer.RunLimitMinutes)));
        launch.DeadlineKey.Should().Be(ConfigKeys.Timer.RunLimitMinutes.Name);
        TaskWorkers.DeadlineKey(WindowsTask.Cleanup).Should().Be(ConfigKeys.Timer.RunLimitMinutes);
        arguments[1].Should().Be(new WslCare.Core.Actions.WindowsTemp.WindowsTempAction().Id.Text, "the table spells the action id literally; this holds the two together");
    }

    [Fact]
    public void The_cleanup_entry_never_asks_for_the_archive_body_s_decision()
    {
        var launch = TaskWorkers.For(WindowsTask.Cleanup, Self, json: true, Defaults, NeverAsked);

        launch.Should().BeOfType<Reading<WorkerLaunch>.Available>("a cleanup takes no archive decision: that would write the COMMON dry-run stamp");
    }

    [Theory]
    [InlineData("")]
    [InlineData("wsl-care.exe")]
    public void A_worker_is_never_named_by_a_path_that_is_not_absolute(string self)
    {
        TaskWorkers.For(WindowsTask.Watch, self, json: true, Defaults, NeverAsked).Should().BeOfType<Reading<WorkerLaunch>.Unavailable>()
            .Which.Reason.Should().Contain("absolute path");
    }
}
