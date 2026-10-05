using System.Text.Json;
using System.Text.Json.Nodes;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Config;
using WslCare.Core.Hosting;
using WslCare.Core.Json;

namespace WslCare.Scenarios;

/// <summary>
/// Plan §15q R1 end to end, through the BUILT CLI: no <c>config set</c> can widen what root does (the families list closed,
/// a machine-only key refused), and a root run — root claimed in the sandbox, the target user <c>me</c> — trusts the target
/// user's layer only as far as WSL interop makes that user root anyway: without the interop entry, a loosening value is not
/// taken and every answer says so.
/// </summary>
public sealed class ConfigTrustFlows
{
    private const string LooseningLayer = """{ "dryRun": false, "containers": { "stoppedOlderThanDays": 30 }, "auto": { "A5": true } }""";

    [Fact]
    public async Task Config_set_refuses_a_family_list_that_would_widen_A11_and_writes_nothing()
    {
        using var home = new ScenarioHome("cfg-families");

        var refused = await home.RunAsync("config", "set", "processes.families", "testhost,other");

        refused.Exit.Should().Be((int)ExitCode.Usage);
        CliStderr.Of(refused).Messages.Should().ContainSingle().Which.Should().Contain("processes.families").And.Contain("\"other\"").And.Contain("dotnet-build-servers");
        File.Exists(home.Paths.UserConfigFile).Should().BeFalse("a refused value writes nothing");
    }

    [Fact]
    public async Task Config_set_refuses_a_machine_only_key_naming_the_machine_layer()
    {
        using var home = new ScenarioHome("cfg-machine-only");

        var refused = await home.RunAsync("config", "set", "archive.baseFolder", "/srv/archive");

        refused.Exit.Should().Be((int)ExitCode.Usage);
        CliStderr.Of(refused).Messages.Should().ContainSingle().Which.Should().Contain("archive.baseFolder").And.Contain("machine layer");
        File.Exists(home.Paths.UserConfigFile).Should().BeFalse();
    }

    [Fact]
    public async Task With_interop_a_root_run_takes_the_target_users_layer_as_their_intent()
    {
        using var home = RootWorld("cfg-root-interop");
        if (home is null)
        {
            Assert.Skip("a root run reads the TARGET user's layer inside the distro only: the Linux legs");
        }

        var report = Report(await home.RunAsync("config", "get", "--json"));

        Value(report, "dryRun").GetBoolean().Should().BeFalse();
        Value(report, "auto.A5").GetBoolean().Should().BeTrue();
        report.ConfigNotices.Should().BeNull("every user value was taken");
    }

    [Fact]
    public async Task Without_interop_a_root_run_takes_only_the_tightening_values_and_every_answer_says_why()
    {
        using var home = RootWorld("cfg-root-no-interop");
        if (home is null)
        {
            Assert.Skip("a root run reads the TARGET user's layer inside the distro only: the Linux legs");
        }

        File.Delete(home.InteropEntry);

        var config = await home.RunAsync("config", "get", "--json");
        var status = await home.RunAsync("status", "--json");
        var report = Report(config);

        config.Exit.Should().Be((int)ExitCode.Ok);
        report.ObserveOnly.Should().BeFalse("a value root does not take is a notice, not an error");
        Value(report, "dryRun").GetBoolean().Should().BeTrue();
        Value(report, "auto.A5").GetBoolean().Should().BeFalse();
        Value(report, "containers.stoppedOlderThanDays").GetInt32().Should().Be(30, "a longer age only tightens");
        report.ConfigNotices!.Select(n => n.Key).Should().BeEquivalentTo(["dryRun", "auto.A5"]);
        report.ConfigNotices!.Should().OnlyContain(n => n.Message.Contains("WSL interop") && n.Message.Contains("/etc/wsl-care/config.json"));
        var statusJson = JsonNode.Parse(status.Stdout)!;
        statusJson["configNotices"].Should().NotBeNull("status says which user values the run did not take");
        statusJson["configNotices"]!.AsArray().Should().HaveCount(2);
        statusJson["userLayerDigest"].Should().NotBeNull("status names the user layer it read");
        ((string)statusJson["userLayerDigest"]!).Should().MatchRegex("^[0-9a-f]{64}$");
    }

    /// <summary>Root claimed, a target user <c>me</c>, their layer loosening three root-effective keys; <c>null</c> on the
    /// Windows binary, which has no target user.</summary>
    private static ScenarioHome? RootWorld(string purpose)
    {
        var home = new ScenarioHome(purpose) { ClaimsRoot = true };
        if (home.Paths.Side != HostSide.Wsl)
        {
            home.Dispose();
            return null;
        }

        Write(home.Paths.DistroPath("/etc/passwd"), "root:x:0:0::/root:/bin/bash\nme:x:1000:1000::/home/me:/bin/bash\n");
        Write(home.Paths.DistroPath("/home/me/.config/wsl-care/config.json"), LooseningLayer);
        return home;
    }

    private static JsonElement Value(ConfigReport report, string key) => report.Values.Single(v => v.Key == key).Value;

    private static ConfigReport Report(TestSupport.ChildResult result) =>
        JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.ConfigReport) ?? throw new InvalidOperationException("no config report");

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
