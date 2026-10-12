using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Mcp;
using WslCare.Core.Records;
using WslCare.Core.Watch;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Watch;

/// <summary>
/// WR-D4 of PLAN_windows_runner.md §3.2: the Windows binary's <c>watch</c> over a sandboxed Windows layout, with a SCRIPTED process
/// table and boot (nothing of this machine is read). Under the run lock it samples the Windows MCP servers into the user's own CPU
/// ledger and sweeps the run records' retention; with <c>--timer</c> it reaches the A22 hook, which acts on nothing until S7b.3 builds
/// A22 — so the Windows watch writes no history line (S2b's rule).
/// </summary>
public sealed class WindowsWatchRunTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly SandboxHost _sandbox = new("windows-watch");
    private readonly ScriptedBoot _boot = new();

    public void Dispose() => _sandbox.Dispose();

    private static void OnWindowsOnly() =>
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows watch runs over a Windows layout: the Windows legs");

    /// <summary>One claude.exe and one coai-mcp.exe under it, two hours old, idle.</summary>
    private sealed class ScriptedTable : IWindowsProcessTable
    {
        private static readonly WindowsProcessDetails Idle =
            new(Reading.Of(Now.AddHours(-2)), Reading.Of(TimeSpan.FromSeconds(3)), Reading.Of(40_000_000L), Reading.Of(20_000_000L), Reading.Of(1));

        public Reading<IReadOnlyList<WindowsProcessEntry>> List() => Reading.Of<IReadOnlyList<WindowsProcessEntry>>([new(100, 1, "claude.exe"), new(200, 100, "coai-mcp.exe")]);

        public WindowsProcessDetails Details(int pid) => Idle;
    }

    /// <summary>Windows' boot counter and unbiased clock, scripted.</summary>
    private sealed class ScriptedBoot : IWindowsBoot
    {
        public long Milliseconds { get; set; } = 7_200_000;

        public Reading<string> BootId() => Reading.Of("windows-117");

        public Reading<long> UnbiasedMilliseconds() => Reading.Of(Milliseconds);
    }

    private string LedgerFile => _sandbox.Paths.Rules.Join(_sandbox.Paths.StateDirectory, McpCpuLedger.FileName);

    private Task<WatchResult> Watch(bool timer = true, IFileSystem? through = null)
    {
        var clock = new FixedTimeProvider(Now);
        var files = through ?? _sandbox.Files;
        var context = new WatchContext(_sandbox.Paths, files, new RecordingCommandRunner(), clock, new FakeProbe(HostSide.Windows, clock),
            ConfigLoader.Load(_sandbox.Paths, files), new FakeProcessTable(), 4242, ActionRegistry.Product, timer)
        {
            WindowsProcesses = new ScriptedTable(),
            WindowsBoot = _boot,
            Wait = (_, _) => Task.CompletedTask,
        };
        return WatchRun.RunAsync(context, CancellationToken.None);
    }

    private IReadOnlyList<RunRecord> History() => RunHistory.Read(_sandbox.Paths, _sandbox.Files).Records;

    [Fact]
    public async Task The_windows_watch_writes_the_ledger_and_no_history_line()
    {
        OnWindowsOnly();

        var first = await Watch();
        _boot.Milliseconds += 300_000;
        var second = await Watch();

        first.Outcome.Should().Be(WatchOutcome.Sampled, first.Reason);
        first.Ledger.Should().Be(new McpCpuBaseline(LedgerFile, true, string.Empty), "the user's own ledger, written by an unelevated watch");
        second.Ledger.Recorded.Should().BeTrue(second.Ledger.Reason);
        var ledger = McpCpuLedger.Read(_sandbox.Files, McpCpuLedgerPlace.ForWindowsStatus((WindowsHostPaths)_sandbox.Paths, elevated: false), 1 << 20);
        ledger.Entries.Single().Points.Select(p => p.MonotonicMs).Should().Equal([7_200_000L, 7_500_000L], "the ledger advances every period");
        History().Should().BeEmpty("until A22 the Windows watch samples only and writes no history line");
        File.Exists(RunHistory.File(_sandbox.Paths)).Should().BeFalse();
        Directory.Exists(RunDetailStore.Root(_sandbox.Paths)).Should().BeFalse("no run detail either");
        second.History.Should().Be(WatchRun.NoAgentHistory, "the agents' CPU history is the distro's evidence");
    }

    [Fact]
    public async Task The_windows_watch_sweeps_run_retention()
    {
        OnWindowsOnly();
        var aged = Run(Now.AddDays(-RunRetention.RetentionDays - 1));
        var young = Run(Now.AddDays(-1));

        var result = await Watch();

        result.Swept.Should().ContainSingle().Which.HistoryLinesRemoved.Should().Be(1);
        File.Exists(RunDetailStore.Absolute(_sandbox.Paths, aged)).Should().BeFalse("the aged run's detail went with its line");
        File.Exists(RunDetailStore.Absolute(_sandbox.Paths, young)).Should().BeTrue();
        History().Select(r => r.DetailPath).Should().Equal(young);
    }

    /// <summary>The real file system, except that rewriting the history (the retention sweep's first step) throws.</summary>
    private sealed class FailingRewrite(IFileSystem inner, Exception failure) : DelegatingFileSystem(inner)
    {
        public override DeletionVerdict RewriteLines(string path, Func<IReadOnlyList<string>, IReadOnlyList<string>> keep, DeletionScope scope, TimeSpan lockTimeout) =>
            throw failure;
    }

    /// <summary>The second own reviewer's item 6: a sweep that fails costs the sample nothing — the ledger is recorded and the
    /// failure is a problem in the sweep's report.</summary>
    [Theory]
    [InlineData("io")]
    [InlineData("timeout")]
    public async Task A_failing_retention_sweep_keeps_the_sample_and_reports_the_problem(string kind)
    {
        OnWindowsOnly();
        Exception failure = kind == "io" ? new IOException("the history is held") : new TimeoutException("the history lock was not free in time");

        var result = await Watch(through: new FailingRewrite(_sandbox.Files, failure));

        result.Outcome.Should().Be(WatchOutcome.Sampled, result.Reason);
        result.Ledger.Recorded.Should().BeTrue(result.Ledger.Reason);
        result.Swept.Single().Problems.Should().ContainSingle().Which.Should().Contain("run retention failed").And.Contain(failure.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Without_A22_the_timer_branch_acts_on_nothing(bool weekPassed)
    {
        OnWindowsOnly();
        if (weekPassed)
        {
            _sandbox.WriteMachineConfig("""{ "dryRun": false }""");
            Directory.CreateDirectory(_sandbox.Paths.StateDirectory);
            File.WriteAllText(DryRunWindow.File(_sandbox.Paths), JsonSerializer.Serialize(new FirstTimerRun(SchemaVersion.Current, Now.AddDays(-8)), WslCareJsonContext.Default.FirstTimerRun));
        }

        var result = await Watch(timer: true);
        var untimed = await Watch(timer: false);

        result.Outcome.Should().Be(WatchOutcome.Sampled, result.Reason);
        result.Reason.Should().Be(WatchRun.NoWindowsAct, "every gate is open or not, nothing is built to act yet");
        result.Acts.Should().BeEmpty();
        result.Tried.Should().BeEmpty();
        untimed.Reason.Should().Be(WatchRun.NoTimerOnWindows);
        History().Should().BeEmpty("a watch that acts on nothing writes no history line");
        File.Exists(DryRunWindow.File(_sandbox.Paths)).Should().Be(weekPassed, "a watch with nothing to act on never starts the dry-run week");
    }

    /// <summary>One run's history line and detail, started at <paramref name="started"/>.</summary>
    private string Run(DateTimeOffset started)
    {
        var id = RunId.New(started, 100);
        var relative = RunDetailStore.RelativePath(id);
        RunDetailStore.Write(_sandbox.Paths, _sandbox.Files, id, """{"schemaVersion":1}"""u8);
        new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(new RunRecord(1, id, RunTrigger.Timer, started, started, RunOutcome.Completed, [], RunKind.Collect) { Detail = relative });
        return relative;
    }
}
