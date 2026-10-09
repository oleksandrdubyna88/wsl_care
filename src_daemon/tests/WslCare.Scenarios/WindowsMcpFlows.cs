using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Json;

namespace WslCare.Scenarios;

/// <summary>
/// E14 S7a over the BUILT CLI (coai plan round 2026-10-09, finding 1): the Windows binary's <c>status --json</c> carries the
/// <c>windowsMcpServers</c> block, read from the real process table (read-only — a Toolhelp snapshot and query-only handles);
/// the distro's binary carries none.
/// </summary>
public sealed class WindowsMcpFlows
{
    [Fact]
    public async Task Status_json_carries_the_windows_mcp_block_on_windows_and_none_in_the_distro()
    {
        using var home = new ScenarioHome("windows-mcp");

        var result = await home.RunAsync("status", "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        var report = JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.StatusReport)!;
        if (OperatingSystem.IsWindows())
        {
            report.WindowsMcpServers.Should().NotBeNull();
            report.WindowsMcpServers!.Available.Should().BeTrue(report.WindowsMcpServers.Reason);
            report.WindowsMcpServers.Count.Should().BeGreaterThanOrEqualTo(0);
            report.WindowsMcpServers.WindowMilliseconds.Should().Be(1000);
            report.McpServers!.Reason.Should().Contain("windowsMcpServers", "the Windows binary's distro block points at its own");
        }
        else
        {
            report.WindowsMcpServers.Should().BeNull("the distro's binary reads the distro's servers (mcpServers)");
        }

        home.Calls.Should().BeEmpty("counting MCP servers starts no process");
    }
}
