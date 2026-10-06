using System.Text.Json.Nodes;

using FluentAssertions;

using WslCare.Cli;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>E7.S2c: <c>units dropin &lt;unit&gt;</c> renders the drop-in install.sh writes, from the machine configuration — and
/// <c>status --json</c> publishes the daemon values the extension mirrors as <c>limits</c>.</summary>
public sealed class UnitsCommandTests
{
    [Fact]
    public void A_drop_in_says_the_machine_layer_s_timer_period_and_service_limits()
    {
        using var sandbox = new SandboxHost("units-dropin");
        sandbox.WriteMachineConfig("""{ "timer": { "periodHours": 8 }, "units": { "memoryMaxMb": 1536 } }""");

        var (timerExit, timer, timerErr) = CliRun.Over(sandbox, "units", "dropin", "wsl-care.timer");
        var (serviceExit, service, _) = CliRun.Over(sandbox, "units", "dropin", "wsl-care.service");

        timerExit.Should().Be((int)ExitCode.Ok, timerErr);
        CliRun.Lines(timer).Should().Contain(["[Timer]", "OnCalendar=", "OnCalendar=*-*-* 00/8:00:00", "RandomizedDelaySec=5min", "AccuracySec=1min"]);
        serviceExit.Should().Be((int)ExitCode.Ok);
        CliRun.Lines(service).Should().Contain(["[Service]", "Nice=19", "MemoryMax=1536M", "TimeoutStopSec=90"]);
    }

    [Fact]
    public void A_unit_that_has_no_drop_in_is_refused_naming_the_four_that_do()
    {
        using var sandbox = new SandboxHost("units-dropin-unknown");

        var (exit, stdout, stderr) = CliRun.Over(sandbox, "units", "dropin", "sshd.service");

        exit.Should().Be((int)ExitCode.Usage);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("wsl-care.timer").And.Contain("wsl-care-act@.service").And.Contain("wsl-care-events.service");
    }

    /// <summary>E7.S2b/S2c review C-M2: a machine layer in error (a period that does not divide 24) gets NO drop-in — install.sh never
    /// installs one from a configuration the daemon itself refuses.</summary>
    [Fact]
    public void A_drop_in_is_refused_while_the_configuration_is_in_error()
    {
        using var sandbox = new SandboxHost("units-dropin-invalid");
        sandbox.WriteMachineConfig("""{ "timer": { "periodHours": 5 } }""");

        var (exit, stdout, stderr) = CliRun.Over(sandbox, "units", "dropin", "wsl-care.timer");

        exit.Should().Be((int)ExitCode.ObserveOnly);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("timer.periodHours").And.Contain("no drop-in is written");
    }

    /// <summary>E7.S2b/S2c review C-M1: <c>config set</c> evaluates the coupled rules on the configuration the change would produce —
    /// a value the loader would not take is refused, and nothing is written.</summary>
    [Fact]
    public void Config_set_refuses_a_value_that_would_break_a_rule_with_the_machine_layer()
    {
        using var sandbox = new SandboxHost("config-set-rule");
        sandbox.WriteMachineConfig("""{ "logs": { "maxRangeDays": 120 } }""");

        var (exit, _, stderr) = CliRun.Over(sandbox, "config", "set", "runs.historyRetentionDays", "200");

        exit.Should().Be((int)ExitCode.Usage);
        stderr.Should().Contain("logs.maxRangeDays").And.Contain("Nothing was written");
        File.Exists(sandbox.Paths.UserConfigFile).Should().BeFalse();
    }

    /// <summary>E7.S2b/S2c review C-M4: the writer keeps the cap its reader keeps — config.maxLayerBytes in force, not the key's range.</summary>
    [Fact]
    public void Config_set_keeps_the_user_layer_under_the_cap_in_force()
    {
        using var sandbox = new SandboxHost("config-set-cap");
        sandbox.WriteMachineConfig("""{ "config": { "maxLayerBytes": 4096 } }""");
        // A real folder per entry on Linux, where the rules look at it; the shape alone elsewhere.
        const string home = "/home/me";
        foreach (var i in Enumerable.Range(0, 16).Where(_ => sandbox.Paths is Core.Hosting.LinuxHostPaths))
        {
            Directory.CreateDirectory(((Core.Hosting.LinuxHostPaths)sandbox.Paths).DistroPath($"{home}/.tool{i}-data-folder-with-a-long-name"));
        }

        var big = "[" + string.Join(",", Enumerable.Range(0, 16).Select(i => $$"""{"cli":"{{home}}/.local/bin/tool{{i}}","side":"wsl","name":"Tool number {{i}} with a long enough name","dataFolders":["{{home}}/.tool{{i}}-data-folder-with-a-long-name"],"sessionGlob":"sessions/**/*.jsonl"}""")) + "]";

        var host = new CliHost(sandbox.Paths, sandbox.Files, new FixedTimeProvider(), new RecordingCommandRunner()) { StandardInput = () => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(big)) };

        var (exit, _, stderr) = CliRun.Over(host, "config", "set", "aiAgents.extra", "-");

        exit.Should().Be((int)ExitCode.Usage, stderr);
        stderr.Should().Contain("4096-byte cap");
    }

    /// <summary>The extension's <c>shared/daemonLimits.ts</c> (PR #12) reads <c>limits.historyRetentionDays</c> and
    /// <c>limits.requestFutureSkewSeconds</c>: the values in force, the machine layer's when it sets them.</summary>
    [Fact]
    public void Status_json_publishes_the_limits_in_force()
    {
        using var sandbox = new SandboxHost("status-limits");
        sandbox.WriteMachineConfig("""{ "runs": { "historyRetentionDays": 120 }, "requests": { "futureSkewSeconds": 120 } }""");

        var (exit, stdout, stderr) = CliRun.Over(sandbox, "status", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var limits = JsonNode.Parse(stdout)!["limits"]!.AsObject();
        limits["historyRetentionDays"]!.GetValue<int>().Should().Be(120);
        limits["requestFutureSkewSeconds"]!.GetValue<int>().Should().Be(120);
        limits["maxShownNames"]!.GetValue<int>().Should().Be(10_000);
        limits["requestGraceSeconds"]!.GetValue<int>().Should().Be(60);
    }
}
