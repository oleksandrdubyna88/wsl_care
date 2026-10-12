using System.Text;
using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Collect;
using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Json;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions;

/// <summary>
/// 2026-10-12, the first live timer run: about 20 sessions keep the load above <c>idle.cpuPercent</c>, so A13 waited for idle for
/// ever. An action that declares <see cref="ICleanupAction.MaxDeferHours"/> (A13: <c>archive.maxDeferHours</c>) runs on the TIMER
/// despite the load once its live timer deferrals in <c>history.jsonl</c> have lasted that long — the load alone, never an unknown
/// figure or a running build — and its outcome says why. Every other gate keeps its place.
/// </summary>
public sealed class MaxDeferTests : IDisposable
{
    private const int Pid = 4242;
    private static readonly DateTimeOffset Now = FixedTimeProvider.DefaultNow;

    private readonly LinuxSandbox _sandbox = new("max-defer");
    private readonly List<string> _journal = [];
    private readonly ManualTimeProvider _clock = new(Now);
    private readonly FakeProcessTable _processes = new FakeProcessTable().Alive(Pid, Now.AddMinutes(-1));

    public MaxDeferTests()
    {
        _sandbox.Write("/etc/passwd", "root:x:0:0::/root:/bin/bash\nme:x:1000:1000::/home/me:/bin/bash\n");
        _sandbox.Write("/var/lib/wsl-care/first-timer-run.json", "{\"schemaVersion\":1,\"at\":\"2026-09-01T00:00:00+00:00\"}");
        _sandbox.Load(3.9, 3.9, 3.9, cpus: 4);
        UserConfig("""{ "dryRun": false }""");
    }

    public void Dispose() => _sandbox.Dispose();

    private void UserConfig(string json) => _sandbox.Write("/home/me/.config/wsl-care/config.json", json);

    private ActionEngine Engine(params ICleanupAction[] actions) =>
        new(new EngineContext(_sandbox.Paths, _sandbox.Files, new RecordingCommandRunner { Policy = CommandPolicy.Product }, _clock, new LinuxProbe(_sandbox.Files, _sandbox.Paths, _clock),
            ConfigLoader.Load(_sandbox.Paths, _sandbox.Files), _processes, Pid, new ActionRegistry(actions)));

    private ScriptedAction Archive() => new("A13", _journal) { Idle = IdleRule.TimerOnly, MaxDeferHours = ConfigKeys.Archive.MaxDeferHours };

    private static ActRequest Timer(string id = "A13") => new([ActionId.Find(id)!], RunTrigger.Timer, Execute: true);

    private static ActionOutcome Only(ActResult result) => result.Should().BeOfType<ActResult.Done>().Subject.Detail.Actions.Single();

    /// <summary>One history line naming <paramref name="status"/> for <paramref name="id"/>, <paramref name="hoursAgo"/> before now.</summary>
    internal static RunRecord Line(double hoursAgo, string status, string id = "A13", RunTrigger trigger = RunTrigger.Timer, bool? dryRun = false)
    {
        var at = Now.AddHours(-hoursAgo);
        return new RunRecord(SchemaVersion.Current, RunId.New(at, Pid), trigger, at, at.AddMinutes(1), RunOutcome.Completed, [new ActionRecord(id, 0, 0) { Status = status }], RecordedKind.Collect) { DryRun = dryRun };
    }

    private void History(params RunRecord[] records)
    {
        var text = string.Concat(records.OrderBy(r => r.StartedAt).Select(r => JsonSerializer.Serialize(r, WslCareJsonContext.Compact.RunRecord) + "\n"));
        Directory.CreateDirectory(Path.GetDirectoryName(RunHistory.File(_sandbox.Paths))!);
        File.WriteAllText(RunHistory.File(_sandbox.Paths), text, new UTF8Encoding(false));
    }

    private static RunRecord[] DeferredEvery3Hours(double fromHoursAgo, double toHoursAgo, bool? dryRun = false) =>
        [.. Enumerable.Range(0, (int)((fromHoursAgo - toHoursAgo) / 3) + 1).Select(i => Line(fromHoursAgo - (3 * i), ActionStatus.Deferred, dryRun: dryRun))];

    [Fact]
    public async Task A_live_streak_of_load_deferrals_past_the_limit_runs_the_action_and_its_outcome_says_why()
    {
        History(DeferredEvery3Hours(25, 1));

        var outcome = Only(await Engine(Archive()).ExecuteAsync(Timer(), CancellationToken.None));

        outcome.Status.Should().Be(ActionStatus.Ran, outcome.Reason);
        outcome.Reason.Should().StartWith("ran despite the load: A13 waited for idle since ")
            .And.Contain("since 2026-10-01T11:00:00Z (25 h, archive.maxDeferHours 24)", "the streak began 25 h before the fixed now, 2026-10-02T12:00Z")
            .And.Contain("deferred: CPU 97.5 % over the last 5 min is not below idle.cpuPercent 20 %");
        _journal.Should().Equal("preview A13", "run A13");
    }

    [Fact]
    public async Task A_streak_of_exactly_the_limit_runs()
    {
        History(DeferredEvery3Hours(24, 3));

        var outcome = Only(await Engine(Archive()).ExecuteAsync(Timer(), CancellationToken.None));

        outcome.Status.Should().Be(ActionStatus.Ran, "at least the limit: 24 h of 24");
        outcome.Reason.Should().Contain("(24 h, archive.maxDeferHours 24)");
    }

    [Fact]
    public async Task Within_the_limit_the_action_still_waits_for_idle()
    {
        History(DeferredEvery3Hours(23, 2));

        var outcome = Only(await Engine(Archive()).ExecuteAsync(Timer(), CancellationToken.None));

        outcome.Status.Should().Be(ActionStatus.Deferred);
        outcome.Reason.Should().StartWith("deferred: CPU 97.5 %");
        _journal.Should().Equal("preview A13");
    }

    /// <summary>The consultant's contamination case: <c>IdleStop</c> is asked before <c>DryRunStop</c>, so a dry-run week of a busy machine
    /// writes <c>deferred</c> lines — none of them is a wait the live timer did.</summary>
    [Fact]
    public async Task Dry_run_deferrals_are_no_streak_and_the_first_live_busy_run_still_defers()
    {
        History([.. DeferredEvery3Hours(48, 3, dryRun: true), Line(1, ActionStatus.Deferred)]);

        var outcome = Only(await Engine(Archive()).ExecuteAsync(Timer(), CancellationToken.None));

        outcome.Status.Should().Be(ActionStatus.Deferred, "the live streak began an hour ago");
    }

    [Theory]
    [InlineData(ActionStatus.Ran, RunTrigger.Manual)]
    [InlineData(ActionStatus.Skipped, RunTrigger.Timer)]
    [InlineData(ActionStatus.Failed, RunTrigger.Timer)]
    public async Task A_newer_line_of_another_status_ends_the_streak_and_the_wait_counts_from_after_it(string status, RunTrigger trigger)
    {
        History([.. DeferredEvery3Hours(40, 22), Line(20, status, trigger: trigger), .. DeferredEvery3Hours(19, 1)]);

        var outcome = Only(await Engine(Archive()).ExecuteAsync(Timer(), CancellationToken.None));

        outcome.Status.Should().Be(ActionStatus.Deferred, $"the {status} line 20 h ago ended the 40-hour streak");
    }

    [Fact]
    public async Task Lines_that_do_not_name_the_action_neither_end_nor_extend_the_streak()
    {
        History([.. DeferredEvery3Hours(25, 1), Line(12, ActionStatus.Ran, id: "A1"), Line(6, ActionStatus.Skipped, id: "A2")]);

        var outcome = Only(await Engine(Archive()).ExecuteAsync(Timer(), CancellationToken.None));

        outcome.Status.Should().Be(ActionStatus.Ran, outcome.Reason);
    }

    [Fact]
    public async Task A_running_build_still_defers_past_the_limit()
    {
        _sandbox.Load(0.2, 0.2, 0.2, cpus: 4);
        Process(321, "dotnet build src/x.csproj");
        History(DeferredEvery3Hours(30, 1));

        var outcome = Only(await Engine(Archive()).ExecuteAsync(Timer(), CancellationToken.None));

        outcome.Status.Should().Be(ActionStatus.Deferred);
        outcome.Reason.Should().Contain("a build is running");
    }

    [Fact]
    public async Task A_build_beside_a_busy_load_still_defers_past_the_limit()
    {
        Process(321, "dotnet build src/x.csproj");
        History(DeferredEvery3Hours(30, 1));

        var outcome = Only(await Engine(Archive()).ExecuteAsync(Timer(), CancellationToken.None));

        outcome.Status.Should().Be(ActionStatus.Deferred, "the load is reported first, but the build is running too: only the load is ever overridden");
    }

    [Fact]
    public async Task A_limit_of_0_never_runs_despite_the_load()
    {
        UserConfig("""{ "dryRun": false, "archive": { "maxDeferHours": 0 } }""");
        History(DeferredEvery3Hours(70, 1));

        var outcome = Only(await Engine(Archive()).ExecuteAsync(Timer(), CancellationToken.None));

        outcome.Status.Should().Be(ActionStatus.Deferred);
    }

    [Fact]
    public async Task An_unreadable_history_is_no_streak()
    {
        Directory.CreateDirectory(RunHistory.File(_sandbox.Paths));

        var outcome = Only(await Engine(Archive()).ExecuteAsync(Timer(), CancellationToken.None));

        outcome.Status.Should().Be(ActionStatus.Deferred);
    }

    [Fact]
    public async Task An_action_that_declares_no_limit_waits_however_long()
    {
        History(DeferredEvery3Hours(70, 1));

        var outcome = Only(await Engine(new ScriptedAction("A13", _journal) { Idle = IdleRule.TimerOnly }).ExecuteAsync(Timer(), CancellationToken.None));

        outcome.Status.Should().Be(ActionStatus.Deferred);
    }

    [Fact]
    public async Task A_dry_run_past_the_limit_still_runs_nothing()
    {
        UserConfig("""{ "dryRun": true }""");
        History(DeferredEvery3Hours(25, 1));

        var outcome = Only(await Engine(Archive()).ExecuteAsync(Timer(), CancellationToken.None));

        outcome.Status.Should().Be(ActionStatus.DryRun, "the dry-run gate keeps its place after the idle gate");
        _journal.Should().Equal("preview A13");
    }

    private void Process(int pid, string commandLine)
    {
        var name = commandLine.Split(' ')[0];
        _sandbox.Write($"/proc/{pid}/status", $"Name:\t{name}\nState:\tS (sleeping)\nPPid:\t1\nUid:\t1000\t1000\t1000\t1000\nRssAnon:\t100 kB\nRssShmem:\t0 kB\n");
        _sandbox.Write($"/proc/{pid}/stat", $"{pid} ({name}) S 1 {pid} {pid} 0 -1 0 0 0 0 0 50 50 0 0 20 0 1 0 100 0 0\n");
        _sandbox.Write($"/proc/{pid}/cgroup", "0::/user.slice\n");
        File.WriteAllBytes(_sandbox.Paths.DistroPath($"/proc/{pid}/cmdline"), Encoding.UTF8.GetBytes(commandLine.Replace(' ', '\0') + "\0"));
    }
}
