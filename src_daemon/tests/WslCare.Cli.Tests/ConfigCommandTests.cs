using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// The <c>config</c> verbs end to end over a sandboxed host: accepted, refused, the broken user
/// layer reported and repaired, and the JSON contract the extension reads.
/// </summary>
public sealed class ConfigCommandTests
{
    [Fact]
    public void Get_lists_every_key_with_its_value_and_the_default_layer_on_a_fresh_host()
    {
        using var sandbox = new SandboxHost("cfg-get");

        var (exit, stdout, stderr) = CliRun.Over(sandbox, "config", "get");

        exit.Should().Be(0);
        stderr.Should().BeEmpty();
        var lines = CliRun.Lines(stdout);
        lines.Should().HaveCount(Core.Config.ConfigKeys.All.Count);
        lines.Should().Contain(l => l.StartsWith("dryRun") && l.Contains("= true") && l.EndsWith("(default)"));
        lines.Should().Contain(l => l.StartsWith("volumes.anonymousMaxGb") && l.Contains("= 20"));
    }

    [Fact]
    public void Get_of_one_key_prints_that_key_only()
    {
        using var sandbox = new SandboxHost("cfg-get-one");

        var (exit, stdout, _) = CliRun.Over(sandbox, "config", "get", "journal.keepDays");

        exit.Should().Be(0);
        CliRun.Lines(stdout).Should().ContainSingle().Which.Should().Be("journal.keepDays = 30                       (default)");
    }

    [Fact]
    public void Get_of_an_unknown_key_is_a_usage_error_naming_the_key()
    {
        using var sandbox = new SandboxHost("cfg-get-unknown");

        var (exit, stdout, stderr) = CliRun.Over(sandbox, "config", "get", "volumes.anonymousMaxGB");

        exit.Should().Be((int)ExitCode.Usage);
        stdout.Should().BeEmpty();
        CliRun.Lines(stderr).Should().ContainSingle().Which.Should().Contain("volumes.anonymousMaxGB").And.Contain("config get");
    }

    [Fact]
    public void Set_validates_writes_the_user_layer_and_get_then_shows_the_user_layer()
    {
        using var sandbox = new SandboxHost("cfg-set");

        var set = CliRun.Over(sandbox, "config", "set", "volumes.anonymousMaxGb", "25");
        var get = CliRun.Over(sandbox, "config", "get", "volumes.anonymousMaxGb");

        set.Exit.Should().Be(0);
        set.Stderr.Should().BeEmpty();
        set.Stdout.Trim().Should().Be("volumes.anonymousMaxGb = 25                       (user)");
        get.Stdout.Trim().Should().EndWith("(user)");
        sandbox.ReadUserConfig().Should().Contain("\"anonymousMaxGb\": 25");
    }

    [Fact]
    public void Set_refuses_an_out_of_range_value_with_one_stderr_line_and_leaves_the_file_untouched()
    {
        using var sandbox = new SandboxHost("cfg-set-range");
        sandbox.WriteUserConfig("""{ "dryRun": false }""");
        var before = sandbox.ReadUserConfig();

        var (exit, stdout, stderr) = CliRun.Over(sandbox, "config", "set", "thresholds.memAvailableWarnPercent", "250");

        exit.Should().Be((int)ExitCode.Usage);
        stdout.Should().BeEmpty();
        CliRun.Lines(stderr).Should().ContainSingle().Which.Should().Be("wsl-care: thresholds.memAvailableWarnPercent must be a whole number from 0 to 100; got 250");
        sandbox.ReadUserConfig().Should().Be(before);
    }

    [Fact]
    public void Set_refuses_an_unknown_key_and_a_wrongly_typed_value()
    {
        using var sandbox = new SandboxHost("cfg-set-unknown");

        CliRun.Over(sandbox, "config", "set", "nope.key", "1").Exit.Should().Be((int)ExitCode.Usage);
        var typed = CliRun.Over(sandbox, "config", "set", "dryRun", "maybe");

        typed.Exit.Should().Be((int)ExitCode.Usage);
        typed.Stderr.Should().Contain("dryRun must be true or false");
        File.Exists(sandbox.Paths.UserConfigFile).Should().BeFalse("a refused set writes nothing");
    }

    [Fact]
    public void Get_on_a_broken_user_layer_reports_observe_only_with_file_and_line_and_still_answers()
    {
        using var sandbox = new SandboxHost("cfg-broken-get");
        sandbox.WriteUserConfig("{\n  \"dryRun\": false,\n  \"volumes\": { \"anonymousMaxGB\": 1 }\n}");

        var text = CliRun.Over(sandbox, "config", "get");
        var json = CliRun.Over(sandbox, "config", "get", "--json");

        text.Exit.Should().Be(0);
        text.Stderr.Should().Contain($"config error: {sandbox.Paths.UserConfigFile}:3: ").And.Contain("volumes.anonymousMaxGB").And.Contain("observe-only");
        CliRun.Lines(text.Stdout).Should().Contain(l => l.StartsWith("dryRun") && l.Contains("= false") && l.EndsWith("(user)"), "the valid key of the broken file still applies");

        using var report = JsonDocument.Parse(json.Stdout);
        report.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        report.RootElement.GetProperty("observeOnly").GetBoolean().Should().BeTrue();
        var error = report.RootElement.GetProperty("configError").EnumerateArray().Single();
        error.GetProperty("file").GetString().Should().Be(sandbox.Paths.UserConfigFile);
        error.GetProperty("line").GetInt32().Should().Be(3);
        error.GetProperty("message").GetString().Should().Contain("volumes.anonymousMaxGB");
    }

    [Fact]
    public void Set_on_a_broken_user_layer_repairs_it_and_the_next_get_is_valid()
    {
        using var sandbox = new SandboxHost("cfg-broken-set");
        sandbox.WriteUserConfig("""{ "dryRun": false, "volumes": { "anonymousMaxGB": 1 } }""");

        var set = CliRun.Over(sandbox, "config", "set", "npm.maxCacheGb", "7");
        var get = CliRun.Over(sandbox, "config", "get", "--json");

        set.Exit.Should().Be(0);
        set.Stderr.Should().Contain("dropped").And.Contain("volumes.anonymousMaxGB");
        set.Stdout.Trim().Should().StartWith("npm.maxCacheGb = 7");
        using var report = JsonDocument.Parse(get.Stdout);
        report.RootElement.GetProperty("observeOnly").GetBoolean().Should().BeFalse();
        report.RootElement.GetProperty("configError").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void Set_on_a_user_layer_that_is_not_json_moves_it_aside_and_says_where()
    {
        using var sandbox = new SandboxHost("cfg-notjson-set");
        sandbox.WriteUserConfig("not json at all");

        var (exit, _, stderr) = CliRun.Over(sandbox, "config", "set", "dryRun", "false");

        exit.Should().Be(0);
        stderr.Should().Contain("moved to").And.Contain("config.json.broken-");
        CliRun.Over(sandbox, "config", "get", "--json").Stdout.Should().Contain("\"observeOnly\": false");
    }

    [Fact]
    public void Reset_removes_the_key_and_reports_the_effective_value_again()
    {
        using var sandbox = new SandboxHost("cfg-reset");
        sandbox.WriteUserConfig("""{ "dryRun": false }""");

        var reset = CliRun.Over(sandbox, "config", "reset", "dryRun");
        var again = CliRun.Over(sandbox, "config", "reset", "dryRun");

        reset.Exit.Should().Be(0);
        reset.Stdout.Trim().Should().Be("dryRun = true                     (default)");
        again.Exit.Should().Be(0);
        CliRun.Over(sandbox, "config", "reset", "no.such").Exit.Should().Be((int)ExitCode.Usage);
    }

    [Fact]
    public void The_json_report_carries_every_key_with_value_and_layer()
    {
        using var sandbox = new SandboxHost("cfg-json");
        sandbox.WriteMachineConfig("""{ "distro": "Ubuntu-26.04" }""");

        var (exit, stdout, stderr) = CliRun.Over(sandbox, "config", "get", "--json");

        exit.Should().Be(0);
        stderr.Should().BeEmpty();
        using var report = JsonDocument.Parse(stdout);
        var values = report.RootElement.GetProperty("values").EnumerateArray().ToList();
        values.Should().HaveCount(Core.Config.ConfigKeys.All.Count);
        var distro = values.Single(v => v.GetProperty("key").GetString() == "distro");
        distro.GetProperty("value").GetString().Should().Be("Ubuntu-26.04");
        distro.GetProperty("layer").GetString().Should().Be("machine");
        values.Single(v => v.GetProperty("key").GetString() == "processes.families").GetProperty("value").ValueKind.Should().Be(JsonValueKind.Array);
    }

    // Retro gate over PR #4 (code round, F4): the writer knows a reset of an absent key changed nothing — and said nothing.
    [Fact]
    public void Reset_of_a_key_the_user_layer_does_not_hold_says_there_was_nothing_to_remove()
    {
        using var sandbox = new SandboxHost("cfg-reset-absent");
        sandbox.WriteUserConfig("""{ "refreshSeconds": 120 }""");

        var reset = CliRun.Over(sandbox, "config", "reset", "dryRun");
        var set = CliRun.Over(sandbox, "config", "set", "journal.keepDays", "10");

        reset.Exit.Should().Be(0);
        reset.Stderr.Should().Contain("dryRun was not set in the user layer; nothing was removed");
        set.Stderr.Should().NotContain("nothing was removed", "a set of an absent key is the ordinary case, not a no-op");
    }

    [Fact]
    public void A_lossy_repair_says_that_it_switched_the_timer_to_dry_run()
    {
        using var sandbox = new SandboxHost("cfg-repair-pins-dry");
        sandbox.WriteUserConfig("""{ "auto": { "A4": false }, oops }""");

        var set = CliRun.Over(sandbox, "config", "set", "refreshSeconds", "120");

        set.Exit.Should().Be(0);
        set.Stderr.Should().Contain("dryRun was set to true in the user layer");
    }

    [Fact]
    public void Reset_over_an_unparseable_layer_never_claims_the_key_was_absent()
    {
        using var sandbox = new SandboxHost("cfg-reset-broken");
        sandbox.WriteUserConfig("""{ "dryRun": false, oops }""");

        var reset = CliRun.Over(sandbox, "config", "reset", "dryRun");

        reset.Exit.Should().Be(0);
        reset.Stderr.Should().NotContain("was not set in the user layer", "what the unreadable file held is unknown");
    }
}
