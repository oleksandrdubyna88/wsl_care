using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Json;
using WslCare.Core.Mcp;
using WslCare.Core.Status;
using WslCare.Core.Thresholds;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// Plan §15q E7.S2d through the CLI, in-process: <c>status</c> over the captured 2026-10-02 tree, which holds the measured shape
/// itself — the Claude Code extension's native <c>claude</c> running <c>coai-mcp</c>, twice (7203 → 7329, 8290 → 8380) — answers the
/// <c>mcpServers</c> block, its three verdicts NOW and its capability; the Windows binary answers the block unavailable.
/// </summary>
public sealed class McpStatusTests
{
    private static CliHost FixtureHost(Func<TimeSpan, CancellationToken, Task> wait)
    {
        var paths = ProcfsFixture.PathsAt(ProcfsFixture.Root);
        var files = ProcfsFixture.LinkOverlay(new Core.Files.PhysicalFileSystem(paths), ProcfsFixture.Root);
        return new CliHost(paths, files, new FixedTimeProvider(ProcfsFixture.CapturedAt), new RecordingCommandRunner()) { Wait = wait };
    }

    private static StatusReport Report(string stdout) =>
        JsonSerializer.Deserialize(stdout, WslCareJsonContext.Default.StatusReport) ?? throw new InvalidOperationException("status --json printed null");

    [Fact]
    public void Status_json_carries_the_mcp_servers_their_verdicts_and_the_capability()
    {
        var waited = TimeSpan.Zero;
        var host = FixtureHost((window, _) =>
        {
            waited = window;
            return Task.CompletedTask;
        });

        var (exit, stdout, stderr) = CliRun.Over(host, "status", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var report = Report(stdout);
        var mcp = report.McpServers!;
        mcp.Available.Should().BeTrue(mcp.Reason);
        mcp.Count.Should().Be(2);
        mcp.Instances!.Select(i => (i.Pid, i.Owner.Pid)).Should().BeEquivalentTo([(7329, (int?)7203), (8380, (int?)8290)], "each server is owned by the claude session that started it");
        var instance = mcp.Instances!.Single(i => i.Pid == 7329);
        instance.Server.Should().Be("coai-mcp");
        instance.Owner.Should().Be(new McpOwnerReport(false, 7203, "Claude Code", instance.Owner.CommandLine));
        instance.Owner.CommandLine.Should().Contain("native-binary/claude");
        instance.CpuPercent.Value.Should().Be(0, "the fixture's ticks do not move across the window");
        instance.Kind.Should().Be("starting", "no CPU, and the capture was taken about 95 s after boot — younger than the 10-minute idle minimum");
        instance.Activity.Available.Should().BeFalse("the sandbox home holds no coai-mcp log");
        mcp.Servers!.Single(s => s.Name == "coai-mcp").Should().Match<McpServerReport>(s => s.Name == "coai-mcp" && s.StartsBasis == "logNames" && s.Starts.Value == 0);
        waited.Should().Be(TimeSpan.FromMilliseconds(1000), "status waited the default CPU window because an instance has no baseline");
        instance.CpuBasis.Should().Be("window", "a first sighting is measured across the window (plan E14 S1)");
        mcp.CpuBaseline!.Recorded.Should().BeFalse("the captured tree has no boot id — and that is what keeps status from writing a ledger into the checked-in fixture");
        mcp.CpuBaseline.Reason.Should().Contain("boot id");
        report.Verdicts!.Where(v => v.Id.StartsWith("mcp.", StringComparison.Ordinal)).Select(v => (v.Id, v.Level, v.Basis!.Source))
            .Should().Equal((McpVerdicts.Instances, Level.Ok, VerdictSource.Sample), (McpVerdicts.Cpu, Level.Ok, VerdictSource.Sample), (McpVerdicts.Starts, Level.Ok, VerdictSource.Sample));
        report.Capabilities.Should().Contain(Capabilities.StatusMcpServers);
        report.Limits!.McpCpuWindowMilliseconds.Should().Be(1000);
    }

    [Fact]
    public void Status_text_prints_one_mcp_servers_line()
    {
        var (exit, stdout, _) = CliRun.Over(FixtureHost((_, _) => Task.CompletedTask), "status");

        exit.Should().Be((int)ExitCode.Ok);
        CliRun.Lines(stdout).Should().Contain(l => l.StartsWith("mcp servers: 2 (0 idle, 0 busy without a log write)", StringComparison.Ordinal) && l.Contains("coai-mcp 0 in 10 min", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_windows_binary_answers_no_distro_mcp_servers_and_points_at_its_own_block()
    {
        using var root = new TempRoot("mcp-windows");
        var paths = new Core.Hosting.WindowsHostPaths(Core.Hosting.WindowsEnvironment.Sandboxed(root.Path));
        var sample = new Core.Collectors.ProbeSample(Core.Hosting.HostSide.Windows, ProcfsFixture.CapturedAt, TimeSpan.Zero, Core.Collectors.Reading.Missing<Core.Collectors.VmSample>("windows"), Core.Collectors.Reading.Missing<Core.Collectors.HostSample>("test"));
        var defaults = Core.Config.ConfigLoader.Load([(Core.Config.ConfigLoader.DefaultsFile, new Core.Files.FileReadResult.Content(Core.Config.ConfigLoader.EmbeddedDefaults()))]).Config;

        var result = await McpSampling.SampleAsync(paths, new Core.Files.PhysicalFileSystem(paths), new FixedTimeProvider(), (_, _) => Task.CompletedTask, McpCpuLedgerPlace.ForStatus(paths, root: false), sample, defaults, CancellationToken.None);

        McpServersReport.From(result).Should().Be(McpServersReport.From(Core.Collectors.Reading.Missing<McpSample>(McpServerCollector.WindowsReadsItsOwn)));
        result.ReasonOrEmpty.Should().Contain("windowsMcpServers", "E14 S7a: the Windows binary counts its own side's servers");
    }
}
