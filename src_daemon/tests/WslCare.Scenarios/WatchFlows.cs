using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Mcp;
using WslCare.Core.Records;

namespace WslCare.Scenarios;

/// <summary>
/// Plan E14 S2b over the BUILT CLI: <c>watch --timer</c> as root over the captured 2026-10-02 tree (two <c>claude</c> →
/// <c>coai-mcp</c> sessions) writes root's MCP CPU ledger, writes no history line, and lets A19 stop nothing — the captured servers
/// run on a terminal (pts), which A19 never stops. The acting path — a target, the act recorded — is driven in-process through the
/// real engine (<c>WatchRunTests</c>) and the whole program (<c>UnitSuccessExitTests</c>' watch endings); no test signals a process.
/// </summary>
public sealed class WatchFlows
{
    private static ScenarioHome Home(string purpose, string machineLayer)
    {
        var home = LogsFlows.TimerHome(purpose);
        var bootId = Path.Combine(home.SandboxRoot, "proc", "sys", "kernel", "random", "boot_id");
        Directory.CreateDirectory(Path.GetDirectoryName(bootId)!);
        File.WriteAllText(bootId, "6d1c1c5e-0000-4000-8000-000000000001\n");
        // Root reads the machine layer it trusts (a user's layer may be unread as root).
        if (home.Paths is LinuxHostPaths linux)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linux.MachineConfigFile)!);
            File.WriteAllText(linux.MachineConfigFile, machineLayer);
        }

        home.ClaimsRoot = true;
        return home;
    }

    [Theory]
    [InlineData("""{ "dryRun": false }""", "A19 has no target")]
    [InlineData("""{ "dryRun": true }""", "a dry run")]
    public async Task The_watch_timer_records_roots_ledger_and_stops_nothing_on_the_captured_tree(string layer, string why)
    {
        using var home = Home("watch-timer", layer);
        Assert.SkipWhen(home.Paths.Side == HostSide.Windows, "the watch is the distro's, over the captured /proc: the Linux legs");
        // The dry-run week started 8 days ago: only the dryRun setting decides.
        Directory.CreateDirectory(home.Paths.StateDirectory);
        File.WriteAllText(Core.Actions.Engine.DryRunWindow.File(home.Paths), JsonSerializer.Serialize(new Core.Actions.Engine.FirstTimerRun(1, DateTimeOffset.UtcNow.AddDays(-8)), WslCareJsonContext.Default.FirstTimerRun));

        var watch = await home.RunAsync("watch", "--timer", "--json");

        watch.Exit.Should().Be((int)ExitCode.Ok, watch.Stderr);
        var report = JsonSerializer.Deserialize(watch.Stdout, WslCareJsonContext.Default.WatchReport)!;
        report.Outcome.Should().Be("sampled", report.Reason);
        report.Reason.Should().Contain(why, "the captured servers run on a terminal, which A19 never stops");
        report.LedgerRecorded.Should().BeTrue(report.LedgerReason);
        report.Act.Should().BeNull();
        var files = new PhysicalFileSystem(home.Paths) { OwnersAreThisProcess = true, TrustedStateOwner = RegularFiles.EffectiveUid() };
        McpCpuLedger.Read(files, McpCpuLedgerPlace.ForStatus(home.Paths, root: true), 1 << 20).Entries.Select(e => e.Pid).Should().Contain([7329, 8380], "root's ledger holds the captured servers");
        RunHistory.Read(home.Paths, files).Records.Should().BeEmpty("a watch that stops nothing writes no history line");
        home.Calls.Should().BeEmpty("the watch starts no process");
    }
}
