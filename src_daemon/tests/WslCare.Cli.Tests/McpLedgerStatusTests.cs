using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Files;
using WslCare.Core.Json;
using WslCare.Core.Mcp;
using WslCare.Core.Status;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// Plan E14 S1 through the CLI, in-process: <c>status</c> over a COPY of the captured 2026-10-02 tree (two <c>claude</c> →
/// <c>coai-mcp</c> sessions) with a boot id added, so the CPU ledger is really written. The "status writes nothing" test of the read
/// contract has no MCP instance and therefore cannot see a ledger (own plan review, finding 4); these can.
/// </summary>
public sealed class McpLedgerStatusTests : IDisposable
{
    private readonly TempRoot _root = new("mcp-ledger");

    public McpLedgerStatusTests()
    {
        ProcfsFixture.CopyTo(_root.Path);
        _root.File("proc/sys/kernel/random/boot_id", "6d1c1c5e-0000-4000-8000-0000000000aa\n");
    }

    public void Dispose() => _root.Dispose();

    private Core.Hosting.LinuxHostPaths Paths => ProcfsFixture.PathsAt(_root.Path);

    private CliHost Host(ManualTimeProvider clock, bool root, List<TimeSpan> waits)
    {
        var files = ProcfsFixture.LinkOverlay(new PhysicalFileSystem(Paths) { TrustedStateOwner = RegularFiles.EffectiveUid(), OwnersAreThisProcess = true }, _root.Path);
        return new CliHost(Paths, files, clock, new RecordingCommandRunner())
        {
            Privilege = new Core.Hosting.ProcessPrivilege(root, "a test"),
            Wait = (window, _) =>
            {
                waits.Add(window);
                clock.Advance(window);
                return Task.CompletedTask;
            },
        };
    }

    private static McpServersReport Mcp(string stdout) => JsonSerializer.Deserialize(stdout, WslCareJsonContext.Default.StatusReport)!.McpServers!;

    private string OwnLedger => Paths.Rules.Join(Paths.UserStateDirectory, McpCpuLedger.FileName);

    private IReadOnlyList<string> StateDirectoryFiles() =>
        Directory.Exists(Paths.StateDirectory) ? [.. Directory.GetFiles(Paths.StateDirectory, "*", SearchOption.AllDirectories)] : [];

    [Fact]
    public void An_unprivileged_status_keeps_its_ledger_in_its_own_state_folder_and_never_in_the_state_directory()
    {
        var clock = new ManualTimeProvider(ProcfsFixture.CapturedAt) { SteppedTimestamps = true };
        var waits = new List<TimeSpan>();
        var before = StateDirectoryFiles();

        var (exit, stdout, stderr) = CliRun.Over(Host(clock, root: false, waits), "status", "--json");
        clock.Advance(TimeSpan.FromSeconds(121));
        var (_, second, _) = CliRun.Over(Host(clock, root: false, waits), "status", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        Mcp(stdout).CpuBaseline.Should().Be(new McpCpuBaselineReport(OwnLedger, true, null));
        File.Exists(OwnLedger).Should().BeTrue();
        StateDirectoryFiles().Should().Equal(before, "status never writes the root state directory (plan §15b #3, §15j M3)");
        Mcp(second).Instances!.Should().OnlyContain(i => i.CpuBasis == "interval" && i.CpuIntervalSeconds.Value == 121, "the second status measures over the interval since the first");
        waits.Should().HaveCount(1, "only the first status, which had no baseline, waited the window");
    }

    [Fact]
    public void A_root_status_writes_no_ledger_anywhere()
    {
        // Own plan review, finding 1: every verb run as root is re-homed to the target user, so a root status writing "its own"
        // ledger would put a root-owned file into that user's home.
        var clock = new ManualTimeProvider(ProcfsFixture.CapturedAt) { SteppedTimestamps = true };

        var (exit, stdout, stderr) = CliRun.Over(Host(clock, root: true, []), "status", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        Mcp(stdout).CpuBaseline!.Recorded.Should().BeFalse();
        Mcp(stdout).CpuBaseline!.Reason.Should().Be(McpCpuLedger.ReadOnlyRoot);
        Directory.GetFiles(_root.Path, McpCpuLedger.FileName, SearchOption.AllDirectories).Should().BeEmpty();
    }
}
