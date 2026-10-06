using System.Text.Json;
using System.Text.Json.Nodes;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Hosting;

namespace WslCare.Scenarios;

/// <summary>Plan §15q, the E7.S0 review round, through the BUILT CLI: S4 (root's doctor text carries no terminal control from
/// a user layer), C1 (a refused layer never prunes the logs with the default retention), C2 (an unprivileged answer says
/// which values the root timer will ignore) and C3 (<c>config set</c> repairs a linked layer and prints what it wrote).</summary>
public sealed class ConfigReviewRoundFlows
{
    private const char Esc = '\u001b';

    [Fact]
    public async Task Doctor_text_never_carries_a_terminal_control_sequence_from_the_user_layer()
    {
        using var home = new ScenarioHome("review-doctor-esc");
        home.WriteFile(Path.GetRelativePath(home.WorkingDirectory, home.Paths.UserConfigFile), "{ \"\\u001b]52;c;cHduZWQ=\\u0007\": 1 }");

        var doctor = await home.RunAsync("doctor");

        doctor.Stdout.Should().Contain("observe-only", "the unknown key makes the layer an error doctor reports");
        doctor.Stdout.Should().NotContain(Esc.ToString(), "an OSC 52 sequence would write the admin's clipboard");
    }

    [Fact]
    public async Task A_refused_configuration_never_prunes_the_logs_with_the_default_retention()
    {
        using var home = new ScenarioHome("review-prune");
        home.WriteFile(Path.GetRelativePath(home.WorkingDirectory, home.Paths.MachineConfigFile), "{ \"logging\": { \"retentionDays\": 0 }, not json");
        // Both roots a run may log to (the sandbox's /var/log is writable by the test, so an unprivileged run may use it).
        var olds = LogRoots(home).Select(root => Path.Combine(root, "2020-01-01")).ToList();
        foreach (var old in olds)
        {
            Directory.CreateDirectory(old);
            File.WriteAllText(Path.Combine(old, "wsl-care-00-00-00-1.log"), "an old run\n");
        }

        var status = await home.RunAsync("status", "--json");

        status.Exit.Should().Be((int)ExitCode.Ok);
        JsonNode.Parse(status.Stdout)!["observeOnly"]!.GetValue<bool>().Should().BeTrue();
        olds.Should().OnlyContain(old => Directory.Exists(old), "a run whose configuration was refused does not know the retention the admin chose");
        Directory.EnumerateFiles(home.SandboxRoot, "*.log", SearchOption.AllDirectories).Should().Contain(f => !f.Contains("2020-01-01"), "the run logged, so a prune would have run had the configuration been taken");
    }

    [Fact]
    public async Task Without_interop_an_unprivileged_answer_says_which_user_values_the_root_timer_ignores()
    {
        using var home = new ScenarioHome("review-shadow");
        if (home.Paths.Side != HostSide.Wsl)
        {
            Assert.Skip("WSL interop is the distro's: the Linux legs");
        }

        home.WriteFile(Path.GetRelativePath(home.WorkingDirectory, home.Paths.UserConfigFile), """{ "dryRun": false, "containers": { "stoppedOlderThanDays": 30 } }""");

        var config = JsonNode.Parse((await home.RunAsync("config", "get", "--json")).Stdout)!;

        config["values"]!.AsArray().Single(v => (string)v!["key"]! == "dryRun")!["value"]!.GetValue<bool>().Should().BeFalse("the user's own run takes its own layer");
        config["configNotices"].Should().NotBeNull("the root timer will not take dryRun = false without interop, and the user must be told");
        // coai E7 code round #8: the explanation once (the notice without a key), then each key's short fact.
        config["configNotices"]!.AsArray().Select(n => (string)n!["key"]!).Should().Equal(string.Empty, "dryRun");
        ((string)config["configNotices"]![0]!["message"]!).Should().Contain("root timer").And.Contain("WSL interop");
        ((string)config["configNotices"]![1]!["message"]!).Should().Contain("root timer").And.NotContain("WSL interop");
    }

    [Fact]
    public async Task Config_set_over_a_linked_user_layer_writes_a_regular_file_and_prints_the_value_it_wrote()
    {
        using var home = new ScenarioHome("review-linked-set");
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a file link needs no privilege on Linux; run in WSL or on the Linux legs");
        var dotfile = home.WriteFile("dotfiles/wsl-care.json", """{ "containers": { "stoppedOlderThanDays": 9 } }""");
        Directory.CreateDirectory(Path.GetDirectoryName(home.Paths.UserConfigFile)!);
        File.CreateSymbolicLink(home.Paths.UserConfigFile, dotfile);

        var set = await home.RunAsync("config", "set", "dryRun", "false");

        set.Exit.Should().Be((int)ExitCode.Ok, set.Stderr);
        set.StdoutLines.Should().ContainSingle().Which.Should().Contain("= false").And.EndWith("(user)");
        new FileInfo(home.Paths.UserConfigFile).LinkTarget.Should().BeNull("the layer is a regular file now");
        File.ReadAllText(home.Paths.UserConfigFile).Should().Contain("\"stoppedOlderThanDays\": 9", "the values the link held are kept");
        File.ReadAllText(dotfile).Should().NotContain("dryRun", "the dotfile the link pointed at is not written through");
    }

    private static IReadOnlyList<string> LogRoots(ScenarioHome home) =>
        home.Paths is LinuxHostPaths linux ? [linux.UserLogDirectory, linux.LogDirectory] : [home.Paths.LogDirectory];
}
