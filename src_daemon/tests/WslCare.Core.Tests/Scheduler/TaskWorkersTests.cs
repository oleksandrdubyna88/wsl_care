using FluentAssertions;

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

    [Theory]
    [InlineData(false, new[] { "watch", "--timer" })]
    [InlineData(true, new[] { "watch", "--timer", "--json" })]
    public void The_watch_worker_is_this_binary_s_own_watch_timer_bounded_by_the_watch_s_run_limit(bool json, string[] arguments)
    {
        var launch = TaskWorkers.For(WindowsTask.Watch, Self, json, Defaults).Should().BeOfType<Reading<WorkerLaunch>.Available>().Subject.Value;

        launch.Executable.Should().Be(Self);
        launch.Arguments.Should().Equal(arguments);
        launch.Deadline.Should().Be(TimeSpan.FromMinutes(Defaults.Int(ConfigKeys.McpWatchdog.RunLimitMinutes)));
        TaskWorkers.DeadlineKey(WindowsTask.Watch).Should().Be(ConfigKeys.McpWatchdog.RunLimitMinutes);
    }

    [Fact]
    public void The_archive_worker_is_refused_as_not_built_in_this_version()
    {
        TaskWorkers.For(WindowsTask.Archive, Self, json: true, Defaults).Should().BeOfType<Reading<WorkerLaunch>.Unavailable>()
            .Which.Reason.Should().Be(TaskWorkers.ArchiveNotBuilt);
        TaskWorkers.DeadlineKey(WindowsTask.Archive).Should().Be(ConfigKeys.Timer.RunLimitMinutes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("wsl-care.exe")]
    public void A_worker_is_never_named_by_a_path_that_is_not_absolute(string self)
    {
        TaskWorkers.For(WindowsTask.Watch, self, json: true, Defaults).Should().BeOfType<Reading<WorkerLaunch>.Unavailable>()
            .Which.Reason.Should().Contain("absolute path");
    }
}
