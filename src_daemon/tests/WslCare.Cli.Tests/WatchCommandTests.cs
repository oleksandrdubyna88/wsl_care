using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Files;
using WslCare.Core.Json;
using WslCare.Core.Hosting;
using WslCare.Core.Records;
using WslCare.Core.Watch;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// <c>watch [--timer] [--json]</c> (plan E14 S2b) in-process: the parse, root first (refused whole before the lock or any state), the
/// lock, the JSON answer. What one run samples and when A19 acts is <c>WatchRunTests</c>' (Core).
/// </summary>
public sealed class WatchCommandTests : IDisposable
{
    private static readonly ProcessPrivilege Root = new(true, "a test says so");
    private static readonly ProcessPrivilege NotRoot = new(false, "this process does not run as root (a test)");

    private readonly LinuxSandbox _sandbox = new("watch-cli");

    public void Dispose() => _sandbox.Dispose();

    private CliHost Host(ProcessPrivilege privilege) =>
        new(_sandbox.Paths, _sandbox.Files, new FixedTimeProvider(), new RecordingCommandRunner()) { Privilege = privilege, Processes = new FakeProcessTable() };

    [Theory]
    [InlineData("watch")]
    [InlineData("watch", "--json")]
    [InlineData("watch", "--timer")]
    [InlineData("watch", "--timer", "--json")]
    public void The_watch_takes_the_timer_mark_and_json_each_once(params string[] argv)
    {
        CommandLine.Parse(argv).Should().BeOfType<Request.Watch>().Which.Timer.Should().Be(argv.Contains("--timer"));
    }

    [Theory]
    [InlineData("watch", "--timer", "--timer")]
    [InlineData("watch", "--force")]
    [InlineData("watch", "A19")]
    public void Anything_else_is_a_usage_refusal(params string[] argv)
    {
        CommandLine.Parse(argv).Should().BeOfType<Request.Failed>();
    }

    [Fact]
    public void The_watch_exits_75_when_the_lock_is_held_and_77_when_not_root()
    {
        var notRoot = CliRun.Over(Host(NotRoot), "watch", "--timer");
        Directory.Exists(_sandbox.Paths.StateDirectory).Should().BeFalse("refused whole, before the lock or any state");
        var held = RunLock.TryTake(_sandbox.Paths, _sandbox.Files).Should().BeOfType<ExclusiveLock.Held>().Subject;
        (int Exit, string Stdout, string Stderr) busy;
        using (held.Handle)
        {
            busy = CliRun.Over(Host(Root), "watch", "--timer", "--json");
        }

        notRoot.Exit.Should().Be((int)ExitCode.NeedsRoot, notRoot.Stderr);
        notRoot.Stderr.Should().Contain("needs root");
        busy.Exit.Should().Be((int)ExitCode.Busy, busy.Stderr);
        JsonSerializer.Deserialize(busy.Stdout, WslCareJsonContext.Default.WatchReport)!.Outcome.Should().Be("busy");
    }

    [Fact]
    public void A_watch_without_the_timer_mark_samples_and_says_why_it_did_not_act()
    {
        var (exit, stdout, stderr) = CliRun.Over(Host(Root), "watch", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var report = JsonSerializer.Deserialize(stdout, WslCareJsonContext.Default.WatchReport)!;
        report.Outcome.Should().Be("sampled");
        report.Reason.Should().Be(WatchRun.NoTimer);
        report.Act.Should().BeNull();
        RunHistory.Read(_sandbox.Paths, _sandbox.Files).Records.Should().BeEmpty("a watch that stops nothing writes no history line");
    }

    /// <summary>A host over a sandboxed Windows layout (WR-D4) — built on Windows only (<c>CliHost.ProbeFor</c>); no table, no boot of
    /// this machine is read.</summary>
    private static CliHost WindowsHost(WindowsHostPaths paths, bool elevated) =>
        new(paths, new PhysicalFileSystem(paths), new FixedTimeProvider(), new RecordingCommandRunner())
        {
            Probe = new FakeProbe(HostSide.Windows, new FixedTimeProvider()),
            Privilege = new ProcessPrivilege(elevated, elevated ? "this process is elevated (a test says so)" : "this process is not elevated (a test says so)"),
            Wait = (_, _) => Task.CompletedTask,
        };

    [Fact]
    public void An_elevated_windows_watch_is_refused_as_not_as_root()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a CliHost over a Windows layout is built on Windows only");
        using var root = new TempRoot("watch-elevated");
        var paths = new WindowsHostPaths(WindowsEnvironment.Sandboxed(root.Path));

        var (exit, stdout, stderr) = CliRun.Over(WindowsHost(paths, elevated: true), "watch", "--timer", "--json");

        exit.Should().Be((int)ExitCode.NotAsRoot, stderr);
        stderr.Should().Contain("NOT elevated").And.Contain("Nothing was touched");
        stdout.Should().BeEmpty();
        Directory.Exists(paths.StateDirectory).Should().BeFalse("refused whole, before the lock or any state");
    }

    [Fact]
    public void A_busy_lock_exits_75_as_normal()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a CliHost over a Windows layout is built on Windows only");
        using var root = new TempRoot("watch-busy");
        var paths = new WindowsHostPaths(WindowsEnvironment.Sandboxed(root.Path));
        var files = new PhysicalFileSystem(paths);
        var held = RunLock.TryTake(paths, files).Should().BeOfType<ExclusiveLock.Held>().Subject;
        (int Exit, string Stdout, string Stderr) busy;
        using (held.Handle)
        {
            busy = CliRun.Over(WindowsHost(paths, elevated: false), "watch", "--timer", "--json");
        }

        busy.Exit.Should().Be((int)ExitCode.Busy, busy.Stderr);
        var report = JsonSerializer.Deserialize(busy.Stdout, WslCareJsonContext.Default.WatchReport)!;
        report.Outcome.Should().Be("busy", "another run holds the lock: a normal answer, try at the next period");
        report.LedgerRecorded.Should().BeFalse();
        report.HistoryReason.Should().Be(WatchRun.NoAgentHistory, "Windows keeps no agents' CPU history, busy or not; the busy reason is the Reason (own review m4)");
        File.Exists(paths.Rules.Join(paths.StateDirectory, Core.Mcp.McpCpuLedger.FileName)).Should().BeFalse("nothing was sampled");
    }
}
