using System.Globalization;
using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Actions.Suspects;
using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Json;
using WslCare.Core.Mcp;
using WslCare.Core.Processes;
using WslCare.Core.Records;
using WslCare.Core.Tests.Collectors;
using WslCare.Core.Watch;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Watch;

/// <summary>
/// Plan E14 S2b: the watch timer's run over a synthetic <c>/proc</c> — the Claude Code extension's native <c>claude</c> → a
/// <c>coai-mcp</c> burning half a core with no log write — through the REAL engine and A19, with a signal sender that only records
/// (no process on this machine is ever signalled). Root's ledger and the agents' CPU history are written under the run lock; A19
/// acts only from the timer, with <c>auto.A19</c> on and no dry run, on processes this watch has not tried before, as a recorded
/// <c>act</c>; a watch that stops nothing writes no history line.
/// </summary>
public sealed class WatchRunTests : IDisposable
{
    private const string Coai = "/home/me/.vscode-server/data/User/globalStorage/remsoftdev.connect-other-ais/coai-mcp";
    private const string Claude = "/home/me/.vscode-server/extensions/anthropic.claude-code-2.1.0-linux-x64/resources/native-binary/claude";
    private const string Logs = "/home/me/.local/share/coai-mcp/logs";
    private const int Hz = 100;
    private const long BusyTicksPerRun = 150 * Hz;

    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Boot = DateTimeOffset.FromUnixTimeSeconds(SyntheticProcTree.BootUnixSeconds);

    private readonly SyntheticProcTree _tree = new SyntheticProcTree().MemInfo(1000, 0);
    private readonly ManualTimeProvider _clock = new(Now) { SteppedTimestamps = true };
    private long _ticks = 1000;

    public WatchRunTests()
    {
        _tree.Process(100, 1, "/a", 10, words: ["/home/me/.vscode-server/bin/abc/node"], startTicks: StartTicksFor(TimeSpan.FromHours(9)))
            .Process(200, 100, "/a", 10, words: [Claude, "--output-format", "stream-json"], startTicks: StartTicksFor(TimeSpan.FromHours(2)))
            .Process(300, 200, "/a", 30_000, words: [Coai], startTicks: StartTicksFor(TimeSpan.FromHours(1)), cpuTicks: _ticks);
        var named = Now - TimeSpan.FromHours(1);
        _tree.FileAt(string.Create(CultureInfo.InvariantCulture, $"{Logs}/{named:yyyy-MM-dd}/coai-mcp-{named:HH-mm-ss}-300.log"), Now - TimeSpan.FromMinutes(15));
    }

    public void Dispose() => _tree.Dispose();

    private static long StartTicksFor(TimeSpan age) => (long)((Now - Boot - age).TotalSeconds * Hz);

    private PhysicalFileSystem Files => new(_tree.Paths) { TrustedStateOwner = RegularFiles.EffectiveUid(), OwnersAreThisProcess = true };

    /// <summary>Records what it is asked to end and answers "ended" — nothing is signalled.</summary>
    private sealed class RecordingSignals(Func<Action> hook) : IProcessSignals
    {
        public List<ProcessIdentity> Asked { get; } = [];

        public Task<IReadOnlyList<SignalOutcome>> TerminateAllAsync(IReadOnlyList<ProcessIdentity> processes, TimeSpan grace, CancellationToken cancellationToken)
        {
            hook()();
            Asked.AddRange(processes);
            return Task.FromResult<IReadOnlyList<SignalOutcome>>([.. processes.Select(_ => new SignalOutcome.Ended(NeededKill: false))]);
        }
    }

    private RecordingSignals? _recording;

    private RecordingSignals Signals => _recording ??= new RecordingSignals(() => _signalHook);

    /// <summary>The machine layer, and the dry-run week started 8 days ago (so only <c>dryRun</c> decides).</summary>
    private void Configure(string json)
    {
        _tree.Write("/etc/wsl-care/config.json", json);
        var stamp = JsonSerializer.Serialize(new FirstTimerRun(SchemaVersion.Current, Now - TimeSpan.FromDays(8)), WslCareJsonContext.Default.FirstTimerRun);
        _tree.Write("/var/lib/wsl-care/" + DryRunWindow.FileName, stamp);
    }

    /// <summary>Five minutes on, half a core burnt since, then one watch run.</summary>
    private async Task<WatchResult> Watch(bool timer = true, bool advance = true, IFileSystem? through = null)
    {
        if (advance)
        {
            _clock.Advance(TimeSpan.FromMinutes(5));
            _ticks += BusyTicksPerRun;
        }

        _tree.Stat(300, 200, "coai-mcp", 0, StartTicksFor(TimeSpan.FromHours(1)), _ticks);
        var files = through ?? Files;
        var context = new WatchContext(_tree.Paths, files, new RecordingCommandRunner(), _clock, new LinuxProbe(files, _tree.Paths, _clock),
            ConfigLoader.Load(_tree.Paths, files), new FakeProcessTable().Alive(4242, Now - TimeSpan.FromMinutes(1)), 4242, ActionRegistry.Product, timer)
        {
            Signals = this.Signals,
            Wait = (window, _) =>
            {
                _clock.Advance(window);
                return Task.CompletedTask;
            },
        };
        return await WatchRun.RunAsync(context, CancellationToken.None);
    }

    private IReadOnlyList<RunRecord> History() => RunHistory.Read(_tree.Paths, Files).Records;

    [Fact]
    public async Task The_watch_samples_into_roots_ledger_and_history_under_the_lock_and_records_no_run_when_nothing_is_stopped()
    {
        var result = await Watch(advance: false);

        result.Outcome.Should().Be(WatchOutcome.Sampled, result.Reason);
        result.Ledger.Recorded.Should().BeTrue(result.Ledger.Reason);
        result.History.Should().BeEmpty("the agents' CPU history (A19's idle evidence) is recorded too");
        File.Exists(_tree.Paths.Rules.Join(_tree.Paths.StateDirectory, McpCpuLedger.FileName)).Should().BeTrue("root's ledger, the busy evidence");
        AgentCpuHistory.Read(_tree.Paths, Files).Entries.Select(e => e.Pid).Should().Contain([200, 300]);
        History().Should().BeEmpty("a watch that stops nothing writes no history line");
    }

    [Fact]
    public async Task The_watch_acts_only_with_the_timer_flag_auto_on_and_no_dry_run_and_never_tries_one_process_twice()
    {
        Configure("""{ "dryRun": false, "mcpWatchdog": { "busyMinutes": 10 } }""");
        await Watch(advance: false);
        await Watch();
        await Watch();

        var acted = await Watch();
        var again = await Watch();

        acted.Outcome.Should().Be(WatchOutcome.Acted, acted.Reason);
        acted.Tried.Should().Equal(string.Create(CultureInfo.InvariantCulture, $"300:{StartTicksFor(TimeSpan.FromHours(1))}"));
        Signals.Asked.Should().ContainSingle().Which.Pid.Should().Be(300, "busy without a log write for 10 min over three interval readings");
        var line = History().Should().ContainSingle("one recorded act run for the stop").Subject;
        (line.Trigger, line.Kind).Should().Be((RunTrigger.Timer, RunKind.Act));
        line.Actions.Single(a => a.Id == "A19").Status.Should().Be(ActionStatus.Ran);
        again.Outcome.Should().Be(WatchOutcome.Sampled, "the same process is never tried twice by the watch");
        again.Reason.Should().Contain("already tried");
        History().Should().HaveCount(1);
    }

    [Theory]
    [InlineData("no timer flag")]
    [InlineData("auto off")]
    [InlineData("dry run")]
    public async Task A_busy_server_is_left_alone_without_the_timer_flag_with_auto_off_or_in_a_dry_run(string why)
    {
        Configure(why switch
        {
            "auto off" => """{ "dryRun": false, "auto": { "A19": false }, "mcpWatchdog": { "busyMinutes": 10 } }""",
            "dry run" => """{ "dryRun": true, "mcpWatchdog": { "busyMinutes": 10 } }""",
            _ => """{ "dryRun": false, "mcpWatchdog": { "busyMinutes": 10 } }""",
        });
        await Watch(advance: false);
        await Watch();
        await Watch();

        var result = await Watch(timer: why != "no timer flag");

        result.Outcome.Should().Be(WatchOutcome.Sampled);
        result.Reason.Should().Contain(why switch { "auto off" => "auto.A19", "dry run" => "dry", _ => "--timer" });
        Signals.Asked.Should().BeEmpty();
        History().Should().BeEmpty();
    }

    [Fact]
    public async Task The_watch_records_nothing_when_another_run_holds_the_lock()
    {
        var held = RunLock.TryTake(_tree.Paths, Files).Should().BeOfType<ExclusiveLock.Held>().Subject;
        using (held.Handle)
        {
            var result = await Watch(advance: false);

            result.Outcome.Should().Be(WatchOutcome.Busy);
            File.Exists(_tree.Paths.Rules.Join(_tree.Paths.StateDirectory, McpCpuLedger.FileName)).Should().BeFalse("nothing is sampled while another run holds the lock");
        }
    }

    [Fact]
    public async Task A_crash_after_choosing_its_targets_never_lets_the_watch_try_them_again()
    {
        // coai plan round 2026-10-09, finding 1: the identities are written BEFORE the act runs, so a watch killed between the two
        // (here: at the signal) does not try the same process again five minutes later.
        Configure("""{ "dryRun": false, "mcpWatchdog": { "busyMinutes": 10 } }""");
        var seenAtSignal = new List<string>();
        _signalHook = () => seenAtSignal.AddRange(WatchTries.Read(_tree.Paths, Files).Tried);
        await Watch(advance: false);
        await Watch();
        await Watch();
        await Watch();

        var tried = WatchTries.Read(_tree.Paths, Files);

        seenAtSignal.Should().ContainSingle(t => t.StartsWith("300:", StringComparison.Ordinal), "the try was on disk before the signal was sent");
        tried.Tried.Should().ContainSingle().Which.Should().StartWith("300:");
        tried.BootId.Should().Be(SyntheticProcTree.FirstBootId);
    }

    private Action _signalHook = static () => { };

    /// <summary>The run lock, busy from its <paramref name=busyFrom/>-th take on — another run took it between two of the watch's.</summary>
    private sealed class LockTakenLater(IFileSystem inner, int busyFrom) : DelegatingFileSystem(inner)
    {
        private int _takes;

        public override ExclusiveLock TryLockExclusive(string lockPath) =>
            ++_takes >= busyFrom ? new ExclusiveLock.Busy("a full run took it (a test)") : base.TryLockExclusive(lockPath);
    }

    /// <summary>The lock lost as above, and the tries file refuses its second write — the give-back.</summary>
    private sealed class GiveBackRefused(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        private int _takes;
        private int _triesWrites;

        public override ExclusiveLock TryLockExclusive(string lockPath) =>
            ++_takes >= 2 ? new ExclusiveLock.Busy("a full run took it (a test)") : base.TryLockExclusive(lockPath);

        public override Core.Files.Deletion.DeletionVerdict WritePrivateFileAtomically(string path, ReadOnlySpan<byte> content, Core.Files.Deletion.DeletionScope scope) =>
            path.EndsWith(WatchTries.FileName, StringComparison.Ordinal) && ++_triesWrites >= 2
                ? new Core.Files.Deletion.DeletionVerdict.Refused(Core.Files.Deletion.DeletionRule.OutsideDeclaredRoot, "refused by a test")
                : base.WritePrivateFileAtomically(path, content, scope);
    }

    [Fact]
    public async Task A_give_back_that_cannot_be_written_says_so()
    {
        // coai code round 2026-10-09, finding 2: the give-back's write was ignored, so a lost race could still use up the try unsaid.
        Configure("""{ "dryRun": false, "mcpWatchdog": { "busyMinutes": 10 } }""");
        await Watch(advance: false);
        await Watch();
        await Watch();

        var collided = await Watch(through: new GiveBackRefused(Files));

        collided.Outcome.Should().Be(WatchOutcome.Sampled);
        collided.Reason.Should().Contain("could not be given back").And.Contain("refused by a test");
    }

    [Fact]
    public async Task A_lock_taken_between_the_sample_and_the_act_does_not_use_up_the_try()
    {
        Configure("""{ "dryRun": false, "mcpWatchdog": { "busyMinutes": 10 } }""");
        await Watch(advance: false);
        await Watch();
        await Watch();

        var collided = await Watch(through: new LockTakenLater(Files, busyFrom: 2));
        var next = await Watch();

        collided.Outcome.Should().Be(WatchOutcome.Sampled, "the act never ran: another run held the lock");
        collided.Reason.Should().Contain("lock");
        Signals.Asked.Should().ContainSingle("the next watch tried the server, which a lost race must not have used up");
        next.Outcome.Should().Be(WatchOutcome.Acted, next.Reason);
    }
}
