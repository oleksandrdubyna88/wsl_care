using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Actions.Suspects;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Json;

namespace WslCare.Scenarios;

/// <summary>
/// E14 S2a over the BUILT CLI (the owner's decision of 2026-10-08): the timer's full run — every <c>auto</c> on (A19 is on by
/// default), dry run off — records the MCP servers of the captured tree (two <c>claude</c> → <c>coai-mcp</c> sessions) in the CPU
/// history A19 judges by, and stops none of them on a first sighting; a button's <c>act A19 --preview</c> right after selects
/// nothing (one sample is no measured idle time) and writes no state.
/// </summary>
public sealed class McpServerStopFlows
{
    private const string AllOn = """
        { "dryRun": false, "mcpWatchdog": { "idleMinutes": 10, "orphanIdleMinutes": 1 },
          "auto": { "A1": true, "A2": true, "A3": true, "A4": true, "A5": true, "A5Testcontainers": true, "A6": true, "A6Unused": true, "A7": true,
                    "A8": true, "A9": true, "A10": true, "A11": true, "A12": true, "A13": true, "A14": true, "A15": true, "A16": true, "A17": true, "A19": true } }
        """;

    [Fact]
    public async Task The_timer_records_the_mcp_servers_and_stops_none_on_a_first_sighting_and_a_preview_after_it_selects_nothing()
    {
        using var home = LogsFlows.TimerHome("a19-timer");
        Assert.SkipWhen(home.Paths.Side == HostSide.Windows, "A19 is the distro's action, over the captured /proc: the Linux legs");
        var bootId = Path.Combine(home.SandboxRoot, "proc", "sys", "kernel", "random", "boot_id");
        Directory.CreateDirectory(Path.GetDirectoryName(bootId)!);
        File.WriteAllText(bootId, "6d1c1c5e-0000-4000-8000-000000000001\n");
        Directory.CreateDirectory(Path.GetDirectoryName(home.Paths.UserConfigFile)!);
        File.WriteAllText(home.Paths.UserConfigFile, AllOn);

        var collect = await home.RunAsync("collect", "--timer", "--json");

        collect.Exit.Should().Be((int)ExitCode.Ok, collect.Stderr);
        var pass = JsonSerializer.Deserialize(collect.Stdout, WslCareJsonContext.Default.CollectReport)!.Detail!.TimerPass!;
        pass.Ran.Should().BeTrue(pass.Reason);
        var a19 = pass.Actions.Should().ContainSingle(a => a.Id == "A19", "auto.A19 is on, so the timer selects it").Subject;
        a19.Run.Should().BeNull("a first sighting is no measured idle time: nothing is stopped");
        var history = AgentCpuHistory.Read(home.Paths, new PhysicalFileSystem(home.Paths) { OwnersAreThisProcess = true, TrustedStateOwner = Core.Files.RegularFiles.EffectiveUid() });
        history.Entries.Select(e => e.Pid).Should().Contain([7329, 8380], "the timer's run records the captured tree's MCP servers beside the agents");
        var before = await File.ReadAllBytesAsync(AgentCpuHistory.File(home.Paths), TestContext.Current.CancellationToken);

        home.ClaimsRoot = true;
        var preview = await home.RunAsync("act", "A19", "--preview", "--json");

        preview.Exit.Should().Be((int)ExitCode.Ok, preview.Stderr);
        var a19Preview = JsonSerializer.Deserialize(preview.Stdout, WslCareJsonContext.Default.ActReport)!.Actions.Single();
        a19Preview.Preview!.Count.Should().Be(0, "one recorded sample is no measured idle time");
        (await File.ReadAllBytesAsync(AgentCpuHistory.File(home.Paths), TestContext.Current.CancellationToken)).Should().Equal(before, "a preview writes no state");
    }

    /// <summary>E14 S2c (the owner's Q-M2; coai plan round 2026-10-08, accepted): a program the USER lists in
    /// <c>mcpServers.programs</c> goes the same timer path as a catalogue server — the captured tree holds <c>creds-mcp</c> under
    /// each of the two <c>claude</c> sessions (pids 7327 and 8378), so the full run reports it as a server, records its identity in
    /// the CPU history A19 judges by, and stops nothing on a first sighting.</summary>
    [Fact]
    public async Task A_user_program_from_the_config_is_reported_and_recorded_by_the_timer()
    {
        using var home = LogsFlows.TimerHome("a19-programs");
        Assert.SkipWhen(home.Paths.Side == HostSide.Windows, "A19 is the distro's action, over the captured /proc: the Linux legs");
        var bootId = Path.Combine(home.SandboxRoot, "proc", "sys", "kernel", "random", "boot_id");
        Directory.CreateDirectory(Path.GetDirectoryName(bootId)!);
        File.WriteAllText(bootId, "6d1c1c5e-0000-4000-8000-000000000001\n");
        Directory.CreateDirectory(Path.GetDirectoryName(home.Paths.UserConfigFile)!);
        File.WriteAllText(home.Paths.UserConfigFile, AllOn.Replace("\"dryRun\": false,", "\"dryRun\": false, \"mcpServers\": { \"programs\": [\"creds-mcp\"] },", StringComparison.Ordinal));

        var collect = await home.RunAsync("collect", "--timer", "--json");

        collect.Exit.Should().Be((int)ExitCode.Ok, collect.Stderr);
        var detail = JsonSerializer.Deserialize(collect.Stdout, WslCareJsonContext.Default.CollectReport)!.Detail!;
        var creds = detail.Sample!.McpServers!.Servers!.Should().ContainSingle(s => s.Name == "creds-mcp", "the user listed it").Subject;
        creds.Count.Should().Be(2, "one creds-mcp under each claude session in the capture");
        creds.StartsBasis.Should().Be("liveYounger", "a user program has no known log layout");
        detail.TimerPass!.Actions.Single(a => a.Id == "A19").Run.Should().BeNull("a first sighting is no measured idle time");
        var history = AgentCpuHistory.Read(home.Paths, new PhysicalFileSystem(home.Paths) { OwnersAreThisProcess = true, TrustedStateOwner = Core.Files.RegularFiles.EffectiveUid() });
        history.Entries.Select(e => e.Pid).Should().Contain([7327, 8378, 7329, 8380], "the user's program is recorded beside the catalogue server");
    }
}
