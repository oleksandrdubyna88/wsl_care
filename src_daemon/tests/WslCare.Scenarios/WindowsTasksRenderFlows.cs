using System.Text.Json.Nodes;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Actions.Engine;

namespace WslCare.Scenarios;

/// <summary>
/// WR-D1 end to end over the BUILT CLI (PLAN_windows_runner.md §3.2; the coai code round asked for the flow in the product
/// harness, not only in-process): on the Windows binary <c>tasks render &lt;task&gt; --json</c> answers both tasks — the XML of the
/// user's own principal and its <c>tasks run</c> action, the numbers, the summary — and says it is NOT registrable, because the
/// built binary runs from its build folder, not from a staged copy; it writes nothing (no dry-week stamp is started). On the
/// distro's binary the verb is refused with exit 2 and nothing on stdout.
/// </summary>
public sealed class WindowsTasksRenderFlows
{
    [Theory]
    [InlineData("watch", "mcpWatchdog.periodMinutes")]
    [InlineData("archive", "windowsTasks.archiveEveryHours")]
    [InlineData("cleanup", "windowsTasks.cleanupEveryHours")]
    public async Task The_built_binary_renders_a_task_answers_not_registrable_from_its_build_folder_and_writes_nothing(string task, string cadenceKey)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows binary's verb: the Windows legs (the refusal is the other test)");
        using var home = new ScenarioHome($"tasks-render-{task}");

        var result = await home.RunAsync("tasks", "render", task, "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        var answer = JsonNode.Parse(result.Stdout)!;
        answer["task"]!.GetValue<string>().Should().Be(task);
        answer["registrable"]!.GetValue<bool>().Should().BeFalse("the built binary is not a staged copy under the state directory's bin");
        answer["reason"]!.GetValue<string>().Should().Contain("not a staged copy");
        answer["userSid"]!.GetValue<string>().Should().StartWith("S-1-5-", "the principal is the user running it, never elevated");
        answer["xml"]!.GetValue<string>().Should().Contain($"<Arguments>tasks run {task} --json</Arguments>")
            .And.Contain("<LogonType>InteractiveToken</LogonType>").And.Contain("<RunLevel>LeastPrivilege</RunLevel>");
        answer["numbers"]!.AsArray().Select(n => (string)n!["key"]!).Should().Contain(cadenceKey);
        Directory.EnumerateFiles(home.SandboxRoot, "first-timer-run*.json", SearchOption.AllDirectories).Should().BeEmpty("a render peeks at the dry week (the common one and A23's) and never starts either");
    }

    [Fact]
    public async Task The_distro_binary_refuses_the_render_with_exit_2_and_nothing_on_stdout()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the distro's binary: the Linux legs");
        using var home = new ScenarioHome("tasks-render-linux");

        var result = await home.RunAsync("tasks", "render", "watch", "--json");

        result.Exit.Should().Be((int)ExitCode.Usage);
        result.Stdout.Should().BeEmpty();
        result.Stderr.Should().Contain("Windows binary");
    }
}
