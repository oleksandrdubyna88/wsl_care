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
/// E7.S1 end to end over the BUILT CLI (plan §4.6, §15q D1–D3): <c>agents list --measure</c> finds an agent planted in the
/// sandbox's home by its folder and by a binary on PATH, sizes its folder without <c>memory/</c>, counts its sessions — and
/// never starts the binary (the fake on PATH logs every start); without <c>--measure</c> the sizes come from the newest full
/// run, or say "none" with the way to measure, never 0; a full run records totals and no session name.
/// </summary>
public sealed class AgentsFlows
{
    private const string LinuxOnly = "the daily walk of the agents' folders is the distro's (plan §15q D1): the Linux legs";

    private static AgentsReport Report(ChildResult result) =>
        JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.AgentsReport) ?? throw new InvalidOperationException($"agents list printed null: {result.Stderr}");

    private static AgentReport Claude(AgentsReport report) => report.Agents.Single(a => a.Id == "claude-code");

    /// <summary>A Claude Code folder in the sandbox's home: one session of 100 bytes, a memory file that is never entered.</summary>
    private static void PlantClaude(ScenarioHome home)
    {
        var claude = Path.Combine(home.Paths.Home, ".claude");
        Directory.CreateDirectory(Path.Combine(claude, "projects", "p", "memory"));
        File.WriteAllText(Path.Combine(claude, "projects", "p", "s1.jsonl"), new string('x', 100));
        File.WriteAllText(Path.Combine(claude, "projects", "p", "memory", "notes.md"), new string('m', 5000));
    }

    /// <summary>The fake tool installed as <c>claude</c> on the scenario's PATH: if anything started it, its call is logged.</summary>
    private static void PlantClaudeBinary(ScenarioHome home)
    {
        var target = Path.Combine(home.FakeBin, FakeToolProtocol.FileName("claude", OperatingSystem.IsWindows()));
        File.Copy(ChildProcess.BesideTheTests("wsl-care-fake-tool"), target);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task Agents_list_measure_finds_a_planted_agent_sizes_it_without_memory_and_starts_nothing()
    {
        using var home = new ScenarioHome("agents-measure");
        PlantClaude(home);
        PlantClaudeBinary(home);

        var result = await home.RunAsync("agents", "list", "--measure", "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        var report = Report(result);
        report.SchemaVersion.Should().Be(1);
        report.Sizes.Source.Should().Be("now");
        var claude = Claude(report);
        claude.Tracked.Should().BeTrue();
        // An elevated process (a CI runner) is root to discovery: it looks at folders only, never the user's binaries (plan §15q D3).
        claude.DetectedBy.Should().Equal(Environment.IsPrivilegedProcess ? [AgentDiscovery.Folder] : [AgentDiscovery.Binary, AgentDiscovery.Folder]);
        claude.TotalBytes.Bytes.Should().Be(100, "memory/ is never entered (plan §15q H2)");
        claude.DataFolders.Where(f => f.Exists).Should().ContainSingle("only ~/.claude (%USERPROFILE%\\.claude) is planted; the others say they do not exist")
            .Which.Excluded.Should().Contain("memory (never entered)");
        claude.Sessions.Counted.Should().BeTrue();
        claude.Sessions.Count.Should().Be(1);
        claude.Sessions.LargestSessions.Should().ContainSingle().Which.Name.Should().Be("projects/p/s1.jsonl", "a live answer names the largest sessions");
        claude.Version.Available.Should().BeFalse("nothing names a version on disk, and nothing is executed to ask (plan §15q D3)");
        home.Calls.Should().BeEmpty("no agent binary is ever started — the fake on PATH would have logged it");
        report.Agents.Where(a => a.Id != "claude-code").Should().OnlyContain(a => !a.Tracked, "nothing else is planted in the sandbox");
    }

    [Fact]
    public async Task Agents_list_in_text_names_the_agent_and_how_many_more_were_not_found()
    {
        using var home = new ScenarioHome("agents-text");
        PlantClaude(home);

        var result = await home.RunAsync("agents", "list", "--measure");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        result.Stdout.Should().Contain("measured now").And.Contain("Claude Code").And.Contain("by folder")
            .And.Contain($"{AgentCatalogue.Agents.Count - 1} more catalogue agent(s) not found here");
    }

    [Fact]
    public async Task Agents_list_refuses_an_unknown_option()
    {
        using var home = new ScenarioHome("agents-usage");

        var result = await home.RunAsync("agents", "list", "--everything");

        result.Exit.Should().Be((int)ExitCode.Usage);
        CliStderr.Of(result).Messages.Should().ContainSingle().Which.Should().Contain("--everything");
    }

    /// <summary>Plan §15q D1: before any full run the sizes say "none" and how to measure — never 0; after a collect (the first
    /// full run walks the folders) they are the run's totals, with its age, and no session's name was recorded.</summary>
    [Fact]
    public async Task Without_a_full_run_the_sizes_say_none_and_after_one_they_are_its_totals_without_names()
    {
        using var home = CollectFlows.Captured("agents-full-run");
        Assert.SkipWhen(home.Paths.Side == HostSide.Windows, LinuxOnly);
        PlantClaude(home);

        var before = Report(await home.RunAsync("agents", "list", "--json"));
        (await home.RunAsync("collect", "--json")).Exit.Should().Be((int)ExitCode.Ok);
        var after = Report(await home.RunAsync("agents", "list", "--json"));

        before.Sizes.Source.Should().Be("none");
        before.Sizes.Reason.Should().Contain("agents list --measure");
        Claude(before).TotalBytes.Available.Should().BeFalse("not measured is not 0");
        after.Sizes.Source.Should().Be("fullRun");
        after.Sizes.RunId.Should().NotBeNullOrEmpty();
        Claude(after).TotalBytes.Bytes.Should().Be(100);
        Claude(after).Sessions.Count.Should().Be(1);
        Claude(after).Sessions.LargestSessions.Should().BeNull("a full run records totals only — no session's name (plan §15q D1)");
        home.Calls.Should().NotContain(c => c.Tool == "claude");
        var recorded = Directory.EnumerateFiles(home.Paths.StateDirectory, "*", SearchOption.AllDirectories).Select(File.ReadAllText);
        recorded.Should().NotContain(text => text.Contains("s1.jsonl", StringComparison.Ordinal), "no recorded file names a session");
    }
}
