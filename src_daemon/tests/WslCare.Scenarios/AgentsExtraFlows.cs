using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Agents;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.FakeTool;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// E7.S2 end to end over the BUILT CLI (plan §15q D4, R2): <c>agents probe</c> refused as root with its own exit code and the
/// fix; a probe of a CLI that never starts it; the manual agents of the user layer in <c>agents list</c>, a refused one with
/// its rule; and a root <c>collect</c> that walks an accepted one.
/// </summary>
public sealed class AgentsExtraFlows
{
    private const string LinuxOnly = "the distro's binary probes and walks the distro's CLIs (the Windows ones are E7.S5b): the Linux legs";

    private static AgentsReport Agents(ChildResult result) =>
        JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.AgentsReport) ?? throw new InvalidOperationException($"agents list printed null: {result.Stderr}");

    private static void UserLayer(ScenarioHome home, string extras)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(home.Paths.UserConfigFile)!);
        File.WriteAllText(home.Paths.UserConfigFile, $$"""{ "aiAgents": { "extra": {{extras}} } }""");
    }

    private static string Entry(string name, string folder) =>
        $$"""{ "cli": "/home/me/.local/bin/{{name}}", "side": "wsl", "name": "{{name}}", "dataFolders": ["{{folder}}"], "sessionGlob": "*" }""";

    private static string Under(ScenarioHome home, string distroPath) => Path.Combine(home.SandboxRoot, distroPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task Agents_probe_as_root_is_refused_with_its_own_exit_code_and_the_fix()
    {
        using var home = new ScenarioHome("probe-root") { ClaimsRoot = true };

        var result = await home.RunAsync("agents", "probe", "/home/me/.local/bin/mycli", "--json");

        result.Exit.Should().Be((int)ExitCode.NotAsRoot);
        result.Stdout.Should().BeEmpty();
        CliStderr.Of(result).Messages.Should().ContainSingle().Which.Should().Contain("not as uid 0").And.Contain("--set-default-user");
    }

    /// <summary>Plan §15q D3 / D4: the probe looks at the CLI and never starts it — the CLI here is the fake tool, which logs any start.</summary>
    [Fact]
    public async Task Agents_probe_of_a_cli_answers_what_it_is_and_never_starts_it()
    {
        using var home = new ScenarioHome("probe-cli");
        Assert.SkipWhen(home.Paths.Side == HostSide.Windows, LinuxOnly);
        var cli = Under(home, "/home/me/.local/bin/mycli");
        Directory.CreateDirectory(Path.GetDirectoryName(cli)!);
        File.Copy(ChildProcess.BesideTheTests("wsl-care-fake-tool"), cli);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Directory.CreateDirectory(Under(home, "/home/me/.mycli"));
        File.WriteAllText(Under(home, "/home/me/.mycli/state.db"), new string('x', 64));

        var result = await home.RunAsync("agents", "probe", "/home/me/.local/bin/mycli", "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        var report = JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.AgentProbeReport)!;
        report.Usable.Should().BeTrue(report.Reason);
        report.Suggested!.DataFolders.Should().Equal("/home/me/.mycli");
        home.Calls.Should().BeEmpty("the CLI was looked at, never started");
    }

    [Fact]
    public async Task Agents_list_shows_the_manual_agents_an_accepted_one_measured_and_a_refused_one_with_its_rule()
    {
        using var home = new ScenarioHome("extra-list");
        Assert.SkipWhen(home.Paths.Side == HostSide.Windows, LinuxOnly);
        Directory.CreateDirectory(Under(home, "/home/me/.mycli"));
        File.WriteAllText(Under(home, "/home/me/.mycli/s.log"), new string('x', 70));
        Directory.CreateDirectory(Under(home, "/home/me/.npm"));
        UserLayer(home, $"[{Entry("mycli", "/home/me/.mycli")}, {Entry("npmish", "/home/me/.npm")}]");

        var report = Agents(await home.RunAsync("agents", "list", "--measure", "--json"));

        var mine = report.Agents.Single(a => a.Id == "manual:mycli");
        mine.DetectedBy.Should().Equal(ExtraAgents.Manual);
        mine.TotalBytes.Bytes.Should().Be(70);
        mine.Sessions.Count.Should().Be(1);
        var refused = report.Agents.Single(a => a.Id == "manual:npmish");
        refused.TotalBytes.Available.Should().BeFalse();
        refused.DataFolders.Single().Size.Reason.Should().Contain("not walked").And.Contain("A8's cleanup folder ~/.npm");
        home.Calls.Should().BeEmpty();
    }

    /// <summary>Plan §15q D1 with R2: the root timer's walk takes the TARGET user's manual agents too, judged again as root.</summary>
    [Fact]
    public async Task A_root_collect_walks_an_accepted_manual_agent_and_records_its_totals()
    {
        using var home = CollectFlows.Captured("extra-collect");
        Assert.SkipWhen(home.Paths.Side == HostSide.Windows, LinuxOnly);
        Directory.CreateDirectory(Under(home, "/home/me/.mycli"));
        File.WriteAllText(Under(home, "/home/me/.mycli/s.log"), new string('x', 40));
        UserLayer(home, $"[{Entry("mycli", "/home/me/.mycli")}]");

        (await home.RunAsync("collect", "--json")).Exit.Should().Be((int)ExitCode.Ok);
        var report = Agents(await home.RunAsync("agents", "list", "--json"));

        report.Sizes.Source.Should().Be("fullRun");
        report.Agents.Single(a => a.Id == "manual:mycli").TotalBytes.Bytes.Should().Be(40);
    }
}
