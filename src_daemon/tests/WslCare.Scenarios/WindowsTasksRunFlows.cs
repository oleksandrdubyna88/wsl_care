using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Json;

namespace WslCare.Scenarios;

/// <summary>
/// WR-D3 end to end over the BUILT CLI (PLAN_windows_runner.md §3.2, §3.3): on the Windows binary <c>tasks run watch --json</c> really
/// supervises — it creates its OWN built binary suspended inside a kill-on-close job as <c>watch --timer --json</c>, resumes it there
/// and waits. Since WR-D4 that worker is the Windows watch: inside the SAME <see cref="ScenarioHome"/> sandbox (the worker inherits
/// <c>WSL_CARE_ROOT</c>) it samples into the sandboxed ledger, writes no history line, and its answer is the supervisor's stdout (WR-D3
/// review n4: this flow never starts a watch outside the sandbox). <c>tasks run archive</c> is refused as not built (WR-D5) before
/// anything is started. On the distro's binary the verb is refused with exit 2.
/// </summary>
public sealed class WindowsTasksRunFlows
{
    [Fact]
    public async Task The_built_binary_supervises_its_own_watch_which_samples_into_the_sandbox()
    {
        WindowsWatchFlows.OnTheWindowsLegs();
        using var home = WindowsWatchFlows.Home("tasks-run-watch");

        var result = await home.RunAsync("tasks", "run", "watch", "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, "the supervised worker's own code: the Windows watch sampled; {0}", result.Stderr);
        result.Stderr.Should().Contain("tasks run watch: the worker exited 0");
        WindowsWatchFlows.SampledOnly(home, JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.WatchReport)!);
    }

    [Fact]
    public async Task The_built_binary_refuses_the_archive_task_as_not_built_and_starts_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows binary's verb: the Windows legs");
        using var home = new ScenarioHome("tasks-run-archive");

        var result = await home.RunAsync("tasks", "run", "archive", "--json");

        result.Exit.Should().Be((int)ExitCode.Usage);
        result.Stdout.Should().BeEmpty();
        result.Stderr.Should().Contain("not built in this version").And.NotContain("starting", "no worker was started");
    }

    [Fact]
    public async Task The_distro_binary_refuses_the_run_with_exit_2_and_nothing_on_stdout()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the distro's binary: the Linux legs");
        using var home = new ScenarioHome("tasks-run-linux");

        var result = await home.RunAsync("tasks", "run", "watch", "--json");

        result.Exit.Should().Be((int)ExitCode.Usage);
        result.Stdout.Should().BeEmpty();
        result.Stderr.Should().Contain("Windows binary");
    }
}
