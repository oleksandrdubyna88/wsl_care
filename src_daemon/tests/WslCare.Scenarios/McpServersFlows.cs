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
        report.McpServers.Servers!.Single(s => s.Name == "coai-mcp").Starts.Value.Should().Be(34);
        report.McpServers.Servers!.Single(s => s.Name == "coai-mcp").StartTimes.Should().HaveCount(34, "each start is listed with its own time (the owner, 2026-10-07)").And.OnlyContain(s => !s.Running && s.At <= s.LastWriteAt);
        report.Verdicts!.Single(v => v.Id == McpVerdicts.Starts).Level.Should().Be(Level.Warn, "34 starts in 10 minutes is a restart storm");
        report.Capabilities.Should().Contain(Capabilities.StatusMcpServers);
        home.Calls.Should().BeEmpty("counting MCP servers starts no process");
    }

    /// <summary>E14 S2d (Q13; coai code round 2026-10-08, finding 4): the BUILT CLI over the captured tree counts
    /// <c>npx @playwright/mcp</c> — <c>claude</c> → <c>npm exec</c> → a shell → <c>node …/.bin/playwright-mcp</c> — as one instance per
    /// session, owned by Claude Code, once the shell the capture left out is in the tree; without it (the checked-in capture,
    /// the golden) the two count as not under an agent.</summary>
    [Fact]
    public async Task Status_json_counts_npx_started_playwright_mcp_under_its_agent()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the Linux binary reads the procfs tree");
        using var home = new ScenarioHome("mcp-playwright");
        ProcfsFixture.CopyTo(home.SandboxRoot);
        var proc = Path.Combine(home.SandboxRoot, "proc");
        AddShell(proc, shell: 7471, npm: 7377);
        AddShell(proc, shell: 8476, npm: 8415);

        var result = await home.RunAsync("status", "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        var mcp = JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.StatusReport)!.McpServers!;
        mcp.Servers!.Single(s => s.Name == "playwright-mcp").Should().Match<McpServerReport>(s => s.Count == 2 && s.StartsBasis == "liveYounger");
        mcp.Instances!.Where(i => i.Server == "playwright-mcp").Select(i => i.Pid).Should().BeEquivalentTo([7472, 8477]);
        mcp.Instances!.Where(i => i.Server == "playwright-mcp").Should().OnlyContain(i => i.Owner.Agent == "Claude Code" && !i.Owner.Orphaned);
        mcp.NotUnderAgent.Should().Be(0, "the npm exec launchers and the shells are no servers, and every server now reaches its agent");
    }

    /// <summary>The <c>sh -c playwright-mcp</c> npm exec starts, modelled on its <c>npm exec</c> parent's files.</summary>
    private static void AddShell(string proc, int shell, int npm)
    {
        var from = Path.Combine(proc, npm.ToString(CultureInfo.InvariantCulture));
        var to = Directory.CreateDirectory(Path.Combine(proc, shell.ToString(CultureInfo.InvariantCulture))).FullName;
        var stat = File.ReadAllText(Path.Combine(from, "stat"));
        var afterName = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        afterName[1] = npm.ToString(CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(to, "stat"), string.Create(CultureInfo.InvariantCulture, $"{shell} (sh) {string.Join(' ', afterName)}"));
        var status = File.ReadAllLines(Path.Combine(from, "status")).Select(line => line.Split(':')[0] switch
        {
            "Name" => "Name:\tsh",
            "Pid" or "Tgid" => string.Create(CultureInfo.InvariantCulture, $"{line.Split(':')[0]}:\t{shell}"),
            "PPid" => string.Create(CultureInfo.InvariantCulture, $"PPid:\t{npm}"),
            _ => line,
        });
        File.WriteAllLines(Path.Combine(to, "status"), status);
        File.WriteAllText(Path.Combine(to, "cmdline"), "sh\0-c\0playwright-mcp\0");
        File.WriteAllText(Path.Combine(to, "comm"), "sh\n");
        File.Copy(Path.Combine(from, "cgroup"), Path.Combine(to, "cgroup"));
    }
}
