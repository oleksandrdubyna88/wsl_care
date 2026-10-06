using System.Globalization;
using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Status;
using WslCare.Core.Thresholds;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// Plan §15q E7.S2d end to end: the BUILT CLI's <c>status --json</c> over the captured 2026-10-02 tree — which holds the
/// measured shape, the Claude Code extension's native <c>claude</c> running <c>coai-mcp</c>, twice — with a restart storm laid
/// into the sandbox home's run logs (34 runs named in the last minutes, as measured on 2026-10-06). The real CPU window is
/// waited (the binary has no seam); the fixture's ticks cannot move, so the CPU reads 0.
/// </summary>
public sealed class McpServersFlows
{
    [Fact]
    public async Task Status_json_counts_the_agents_mcp_servers_and_warns_on_a_restart_storm()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the Linux binary reads the procfs tree");
        using var home = new ScenarioHome("mcp-storm");
        ProcfsFixture.CopyTo(home.SandboxRoot);
        var now = DateTimeOffset.UtcNow;
        var logs = Path.Combine(((LinuxHostPaths)home.Paths).Home, ".local", "share", "coai-mcp", "logs");
        for (var i = 0; i < 34; i++)
        {
            var named = now - TimeSpan.FromSeconds(30 + (i * 8));
            var file = Path.Combine(logs, named.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), string.Create(CultureInfo.InvariantCulture, $"coai-mcp-{named:HH-mm-ss}-{40000 + i}.log"));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, "starting\n", TestContext.Current.CancellationToken);
        }

        var result = await home.RunAsync("status", "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        var report = JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.StatusReport)!;
        report.McpServers!.Count.Should().Be(2, "two claude sessions each run one coai-mcp in the capture");
        report.McpServers.Instances!.Should().OnlyContain(i => i.Owner.Agent == "Claude Code" && !i.Owner.Orphaned);
        report.McpServers.Servers!.Single().Starts.Value.Should().Be(34);
        report.Verdicts!.Single(v => v.Id == McpVerdicts.Starts).Level.Should().Be(Level.Warn, "34 starts in 10 minutes is a restart storm");
        report.Capabilities.Should().Contain(Capabilities.StatusMcpServers);
        home.Calls.Should().BeEmpty("counting MCP servers starts no process");
    }
}
