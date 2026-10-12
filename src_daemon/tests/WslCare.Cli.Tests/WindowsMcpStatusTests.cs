using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Collectors;
using WslCare.Core.Hosting;
using WslCare.Core.Mcp;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// E14 S7a through the CLI, in-process, over a Windows layout with a SCRIPTED process table and probe (so the answer never depends on
/// this machine): the text form prints what it measured. coai code round 2026-10-09, finding 10: the text form paid the CPU window
/// and then printed none of it.
/// </summary>
public sealed class WindowsMcpStatusTests
{
    private const long Gib = 1L << 30;

    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 20, 0, TimeSpan.Zero);

    /// <summary>One claude.exe, one coai-mcp.exe under it, two creds-mcp.exe under one wsl.exe — all idle, two hours old.</summary>
    private sealed class ScriptedTable : IWindowsProcessTable
    {
        private static readonly WindowsProcessDetails Idle = new(Reading.Of(Now.AddHours(-2)), Reading.Of(TimeSpan.FromSeconds(3)), Reading.Of(40_000_000L), Reading.Of(20_000_000L), Reading.Of(1));

        public Reading<IReadOnlyList<WindowsProcessEntry>> List() => Reading.Of<IReadOnlyList<WindowsProcessEntry>>(
        [
            new(100, 1, "claude.exe"), new(200, 100, "coai-mcp.exe"), new(500, 1, "wsl.exe"), new(700, 500, "creds-mcp.exe"), new(701, 500, "creds-mcp.exe"),
        ]);

        public WindowsProcessDetails Details(int pid) => Idle;
    }

    [Fact]
    public void Status_text_on_windows_prints_the_windows_mcp_servers_and_the_vmmem_advice()
    {
        // A host over a Windows layout derives its default probe at construction, and off Windows there is none to derive
        // (CliHost.ProbeFor) — the Windows legs run this; the scripted table and probe keep it independent of the machine there.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a CliHost over a Windows layout is built on Windows only");
        using var root = new TempRoot("windows-mcp-text");
        var paths = new WindowsHostPaths(WindowsEnvironment.Sandboxed(root.Path));
        var clock = new FixedTimeProvider(Now);
        var host = new HostSample(Reading.Of(new HostMemory(92 * Gib, 40 * Gib)), Reading.Missing<VolumeUsage>("not measured"), Reading.Of(34 * Gib), Reading.Missing<HostCommit>("not read"), Reading.Missing<long>("not read"));
        var cli = new CliHost(paths, new Core.Files.PhysicalFileSystem(paths), clock, new RecordingCommandRunner())
        {
            Probe = new FakeProbe(HostSide.Windows, clock) { Host = Reading.Of(host) },
            WindowsProcesses = new ScriptedTable(),
            Wait = (_, _) => Task.CompletedTask,
        };

        var (exit, stdout, stderr) = CliRun.Over(cli, "status");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var lines = CliRun.Lines(stdout);
        lines.Should().Contain(l => l.StartsWith("windows mcp servers: 3 (3 idle, 0 orphaned)", StringComparison.Ordinal) && l.Contains("2 under wsl.exe (pid 500)", StringComparison.Ordinal));
        lines.Should().Contain(l => l.StartsWith("vmmem advice: vmmemWSL holds 34.0 GiB", StringComparison.Ordinal));
    }

    /// <summary>Windows' boot counter and unbiased clock, scripted (E14 S7b.1).</summary>
    private sealed class ScriptedBoot : IWindowsBoot
    {
        public long Milliseconds { get; set; } = 7_200_000;

        public Reading<string> BootId() => Reading.Of("windows-117");

        public Reading<long> UnbiasedMilliseconds() => Reading.Of(Milliseconds);
    }

    private static CliHost WindowsHost(WindowsHostPaths paths, IWindowsBoot boot, List<TimeSpan> waits, bool elevated)
    {
        var clock = new FixedTimeProvider(Now);
        return new CliHost(paths, new Core.Files.PhysicalFileSystem(paths), clock, new RecordingCommandRunner())
        {
            Probe = new FakeProbe(HostSide.Windows, clock),
            WindowsProcesses = new ScriptedTable(),
            WindowsBoot = boot,
            Privilege = new ProcessPrivilege(elevated, "a test says so"),
            Wait = (window, _) =>
            {
                waits.Add(window);
                return Task.CompletedTask;
            },
        };
    }

    private static Core.Status.WindowsMcpServersReport WindowsMcp(string stdout) =>
        System.Text.Json.JsonSerializer.Deserialize(stdout, Core.Json.WslCareJsonContext.Default.StatusReport)!.WindowsMcpServers!;

    /// <summary>E14 S7b.1: the second status, five minutes (on the unbiased clock) after the first, measures every instance over the
    /// interval since it — from the ledger the first one wrote in the Windows state directory — and waits no window.</summary>
    [Fact]
    public void A_second_windows_status_measures_over_the_interval_from_its_own_ledger()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a CliHost over a Windows layout is built on Windows only");
        using var root = new TempRoot("windows-mcp-ledger");
        var paths = new WindowsHostPaths(WindowsEnvironment.Sandboxed(root.Path));
        var boot = new ScriptedBoot();
        var waits = new List<TimeSpan>();

        var (exit, first, stderr) = CliRun.Over(WindowsHost(paths, boot, waits, elevated: false), "status", "--json");
        boot.Milliseconds += 300_000;
        var (_, second, _) = CliRun.Over(WindowsHost(paths, boot, waits, elevated: false), "status", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var ledger = paths.Rules.Join(paths.StateDirectory, McpCpuLedger.FileName);
        WindowsMcp(first).CpuBaseline.Should().Be(new Core.Status.McpCpuBaselineReport(ledger, true, null));
        WindowsMcp(first).Instances!.Should().OnlyContain(i => i.CpuBasis == "window");
        WindowsMcp(second).Instances!.Should().OnlyContain(i => i.CpuBasis == "interval" && i.CpuIntervalSeconds.Value == 300);
        waits.Should().HaveCount(1, "only the first status, with no baseline, waited the window");
    }

    [Fact]
    public void An_elevated_windows_status_writes_no_ledger()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a CliHost over a Windows layout is built on Windows only");
        using var root = new TempRoot("windows-mcp-elevated");
        var paths = new WindowsHostPaths(WindowsEnvironment.Sandboxed(root.Path));

        var (exit, stdout, stderr) = CliRun.Over(WindowsHost(paths, new ScriptedBoot(), [], elevated: true), "status", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        WindowsMcp(stdout).CpuBaseline!.Recorded.Should().BeFalse();
        File.Exists(paths.Rules.Join(paths.StateDirectory, McpCpuLedger.FileName)).Should().BeFalse("an elevated status never writes the user's ledger");
        File.Exists(((McpCpuLedgerPlace.WindowsState)McpCpuLedgerPlace.ForWindowsStatus(paths, elevated: true)).LockFile).Should().BeFalse("an elevated status takes no ledger lock (WR-D4)");
    }

    /// <summary>WR-D4 (PLAN_windows_runner.md §3.9): the Windows status takes the SAME ledger lock as the Windows watch; while the watch
    /// holds it, the status answers from the window and records nothing, naming why.</summary>
    [Fact]
    public void A_windows_status_while_the_ledger_lock_is_held_records_nothing_and_says_why()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a CliHost over a Windows layout is built on Windows only");
        using var root = new TempRoot("windows-mcp-locked");
        var paths = new WindowsHostPaths(WindowsEnvironment.Sandboxed(root.Path));
        var place = (McpCpuLedgerPlace.WindowsState)McpCpuLedgerPlace.ForWindowsStatus(paths, elevated: false);
        var held = new Core.Files.PhysicalFileSystem(paths).TryLockExclusive(place.LockFile).Should().BeOfType<Core.Files.ExclusiveLock.Held>().Subject;
        (int Exit, string Stdout, string Stderr) status;
        using (held.Handle)
        {
            status = CliRun.Over(WindowsHost(paths, new ScriptedBoot(), [], elevated: false), "status", "--json");
        }

        status.Exit.Should().Be((int)ExitCode.Ok, status.Stderr);
        var baseline = WindowsMcp(status.Stdout).CpuBaseline!;
        baseline.Recorded.Should().BeFalse();
        baseline.Reason.Should().Contain("ledger lock");
        File.Exists(place.File).Should().BeFalse("a status that does not hold the lock writes nothing");
    }

    /// <summary>E15.S5 through the CLI: the Windows binary judges its OWN host sample — <c>host.commit</c> once, with
    /// <c>basis: sample</c> — and <c>host.vmmem</c>'s reason is the very advice the answer shows as <c>host.vmmemAdvice</c>.</summary>
    [Fact]
    public void The_windows_status_judges_its_own_host_sample_and_the_vmmem_verdict_says_the_advice()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a CliHost over a Windows layout is built on Windows only");
        using var root = new TempRoot("windows-host-verdicts");
        var paths = new WindowsHostPaths(WindowsEnvironment.Sandboxed(root.Path));
        var clock = new FixedTimeProvider(Now);
        var host = new HostSample(
            Reading.Of(new HostMemory(92 * Gib, 40 * Gib)),
            Reading.Missing<VolumeUsage>("not measured"),
            Reading.Of(34 * Gib),
            Reading.Of(new HostCommit(86 * Gib, 100 * Gib)),
            Reading.Of(6 * Gib));
        var cli = new CliHost(paths, new Core.Files.PhysicalFileSystem(paths), clock, new RecordingCommandRunner())
        {
            Probe = new FakeProbe(HostSide.Windows, clock) { Host = Reading.Of(host) },
            WindowsProcesses = new ScriptedTable(),
            Wait = (_, _) => Task.CompletedTask,
        };

        var (exit, stdout, stderr) = CliRun.Over(cli, "status", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var report = System.Text.Json.JsonSerializer.Deserialize(stdout, Core.Json.WslCareJsonContext.Default.StatusReport)!;
        var verdicts = report.Verdicts!;
        verdicts.Where(v => v.Id == "host.commit").Should().ContainSingle()
            .Which.Should().Match<Core.Thresholds.Verdict>(v => v.Level == Core.Thresholds.Level.Warn && v.Basis!.Source == Core.Thresholds.VerdictSource.Sample);
        verdicts.Single(v => v.Id == "host.compression").Level.Should().Be(Core.Thresholds.Level.Ok);
        var vmmem = verdicts.Single(v => v.Id == "host.vmmem");
        vmmem.Level.Should().Be(Core.Thresholds.Level.Warn);
        vmmem.Reason.Should().Be(report.Host.VmmemAdvice).And.StartWith("vmmemWSL holds 34.0 GiB");
    }
}
