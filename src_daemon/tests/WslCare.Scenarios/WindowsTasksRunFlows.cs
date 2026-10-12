using FluentAssertions;

using WslCare.Cli;

namespace WslCare.Scenarios;

/// <summary>
/// WR-D3 end to end over the BUILT CLI (PLAN_windows_runner.md §3.2, §3.3): on the Windows binary <c>tasks run watch --json</c> really
/// supervises — it creates its OWN built binary suspended inside a kill-on-close job as <c>watch --timer --json</c>, resumes it there
/// and waits. The Windows <c>watch</c> is still refused until WR-D4, so the worker exits 2 with its own refusal on the inherited stderr,
/// and the supervisor passes that code through and adds nothing to stdout. <c>tasks run archive</c> is refused as not built (WR-D5)
/// before anything is started. On the distro's binary the verb is refused with exit 2.
/// </summary>
public sealed class WindowsTasksRunFlows
{
    [Fact]
    public async Task The_built_binary_supervises_its_own_watch_and_passes_the_worker_s_refusal_through()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows binary's verb: the Windows legs (the refusal is the other test)");
        using var home = new ScenarioHome("tasks-run-watch");

        var result = await home.RunAsync("tasks", "run", "watch", "--json");

        result.Exit.Should().Be((int)ExitCode.Usage, "the supervised worker's own code: the Windows watch is refused until WR-D4; {0}", result.Stderr);
        result.Stderr.Should().Contain("\"wsl-care watch\" is the distro's", "the WORKER's refusal reaches the inherited stderr — so the worker really ran");
        result.Stdout.Should().BeEmpty("the supervisor writes nothing on stdout; the worker's answer would be there");
    }

    [Fact]
    public async Task The_built_binary_refuses_the_archive_task_as_not_built_and_starts_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows binary's verb: the Windows legs");
        using var home = new ScenarioHome("tasks-run-archive");

        var result = await home.RunAsync("tasks", "run", "archive", "--json");

        result.Exit.Should().Be((int)ExitCode.Usage);
        result.Stdout.Should().BeEmpty();
        result.Stderr.Should().Contain("not built in this version").And.NotContain("is the distro's", "no worker was started");
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
