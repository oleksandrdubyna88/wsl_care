using System.Text.Json;
using System.Text.Json.Nodes;

using FluentAssertions;

using WslCare.Cli;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// E19.S3 end to end over the BUILT CLI (PLAN_disk_hygiene_windows.md §3.3, E19 stories §4): on the Windows binary <c>worktrees scan --json</c>
/// reads the configured roots without starting anything, writes <c>worktrees.json</c> under the state directory, and the Windows
/// <c>status --json</c> reads that report back with its basis; <c>worktrees roots set</c> with nothing on stdin writes nothing. On the
/// distro's binary all three verbs are refused naming the Windows side. The worktrees the Windows legs scan live under a
/// <c>testsToDelete-</c> folder.
/// </summary>
public sealed class WorktreesFlows
{
    [Theory]
    [InlineData("roots", "set", "--json")]
    [InlineData("roots", "clear", "--json")]
    [InlineData("scan", "--json")]
    public async Task The_distro_binary_refuses_the_worktrees_verbs_naming_the_windows_side(params string[] verb)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the distro's binary: the Linux legs");
        using var home = new ScenarioHome("worktrees-linux");

        var result = await home.RunAsync(["worktrees", .. verb]);

        result.Exit.Should().Be((int)ExitCode.Usage);
        result.Stdout.Should().BeEmpty();
        CliStderr.Of(result).Messages.Should().ContainSingle(m => m.Contains("Windows binary", StringComparison.Ordinal) && m.Contains("worktrees", StringComparison.Ordinal));
        Directory.Exists(home.Paths.StateDirectory).Should().BeFalse("a refused verb creates no state");
    }

    [Fact]
    public async Task A_scan_through_the_built_binary_writes_the_report_and_status_reads_it_back_with_its_basis()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows binary's verbs: the Windows legs (the refusal is the other test)");
        using var home = new ScenarioHome("worktrees-scan");
        using var tree = new ToDeleteRoot("scn-worktrees");
        var work = tree.Dir("work");
        tree.File("work/main/.git/HEAD", "ref: refs/heads/main\n");
        tree.File("work/docs/readme.txt", "not a repository\n");
        tree.File("work/gone/.git", "gitdir: C:/no/where/.git/worktrees/gone\n");
        tree.File("work/gone/App.csproj", "<Project/>");
        tree.File("work/gone/bin/Debug/App.dll", new string('d', 700));
        Directory.CreateDirectory(Path.GetDirectoryName(home.Paths.UserConfigFile)!);
        File.WriteAllText(home.Paths.UserConfigFile, new JsonObject { ["worktrees"] = new JsonObject { ["roots"] = new JsonArray(JsonValue.Create(work)) } }.ToJsonString());

        var scan = await home.RunAsync("worktrees", "scan", "--json");

        scan.Exit.Should().Be((int)ExitCode.Ok, scan.Stderr);
        var report = JsonNode.Parse(scan.Stdout)!;
        var root = report["roots"]!.AsArray().Single()!;
        root["mainClones"]!.GetValue<int>().Should().Be(1);
        root["notGit"]!.GetValue<int>().Should().Be(1);
        root["orphans"]!.GetValue<int>().Should().Be(1);
        root["buildOutput"]!["inOrphans"]!["bytes"]!.GetValue<long>().Should().Be(700);
        report["reclaimable"]!["available"]!.GetValue<bool>().Should().BeFalse();
        File.Exists(Path.Combine(home.Paths.StateDirectory, "worktrees.json")).Should().BeTrue();
        home.Calls.Should().BeEmpty("the scan starts no process, not even a fake tool");

        var status = JsonNode.Parse((await home.RunAsync("status", "--json")).Stdout)!;

        status["worktrees"]!["basis"]!["source"]!.GetValue<string>().Should().Be("scan");
        status["worktrees"]!["basis"]!["ageSeconds"]!.GetValue<double>().Should().BeLessThan(600);
        status["verdicts"]!.AsArray().Single(v => v!["id"]!.GetValue<string>() == "worktrees.reclaimable")!["level"]!.GetValue<string>().Should().Be("unknown");
    }

    [Fact]
    public async Task Roots_set_with_nothing_on_stdin_writes_nothing_through_the_built_binary()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows binary's verbs: the Windows legs (the refusal is the other test)");
        using var home = new ScenarioHome("worktrees-roots-empty");

        var set = await home.RunAsync("worktrees", "roots", "set", "--json");

        set.Exit.Should().Be((int)ExitCode.Usage);
        set.Stderr.Should().Contain("Nothing was written.");
        File.Exists(home.Paths.UserConfigFile).Should().BeFalse();
    }

    [Fact]
    public async Task A_scan_with_no_root_says_how_to_choose_folders_through_the_built_binary()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows binary's verbs: the Windows legs (the refusal is the other test)");
        using var home = new ScenarioHome("worktrees-no-root");

        var scan = await home.RunAsync("worktrees", "scan", "--json");

        scan.Exit.Should().Be((int)ExitCode.Ok, scan.Stderr);
        JsonNode.Parse(scan.Stdout)!["roots"]!.AsArray().Should().BeEmpty();
        scan.Stderr.Should().Contain("worktrees roots set");
    }
}
