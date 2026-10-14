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
    private static ArchiveBody.Choice NeverAsked() => throw new InvalidOperationException("the watch asked for the archive body's dry-run decision");

    private static Func<ArchiveBody.Choice> Deciding(bool dry) => () => ArchiveBody.Of(new DryRunDecision(dry, "a test says so"));

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

    [Theory]
    [InlineData("")]
    [InlineData("wsl-care.exe")]
    public void A_worker_is_never_named_by_a_path_that_is_not_absolute(string self)
    {
        TaskWorkers.For(WindowsTask.Watch, self, json: true, Defaults, NeverAsked).Should().BeOfType<Reading<WorkerLaunch>.Unavailable>()
            .Which.Reason.Should().Contain("absolute path");
    }
}
