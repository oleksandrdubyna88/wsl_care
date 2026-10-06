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
