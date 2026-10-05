using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Files;
using WslCare.Core.History;
using WslCare.Core.Json;
using WslCare.Core.Processes;
using WslCare.Core.Records;
using WslCare.Core.Systemd;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Records;

/// <summary>
/// Plan §15o: a run's <c>kind</c> on the history line and in <c>running.json</c> — written by every writer that knows it, read
/// back by one rule — and <c>actions</c> holding per-action results only. The wire of every full-check ending is
/// <c>Cli.Tests/FullCheckLineTests</c>; these are the act's half, the older files, the reading side and <c>logs</c>.
/// </summary>
public sealed class RunKindTests : IDisposable
{
    private static readonly DateTimeOffset Now = FixedTimeProvider.DefaultNow;

    private readonly SandboxHost _sandbox = new("run-kind");

    public void Dispose() => _sandbox.Dispose();

    private IReadOnlyList<RunRecord> History() => RunHistory.Read(_sandbox.Paths, _sandbox.Files).Records;

    private static RunningFile Running(IReadOnlyList<string> actions, string current, RunTrigger trigger, RunKind? kind, int pid = 4321) =>
        new(1, RunId.New(Now.AddMinutes(-9), pid), trigger, actions, current, pid, Now.AddMinutes(-10), Now.AddMinutes(-9), Now.AddMinutes(-5), kind);

    // ---------- running.json written by an older build ----------

    [Theory]
    [InlineData(new[] { "collect" }, "collect", RunKind.Collect)]
    [InlineData(new[] { "collect" }, "", null)]
    [InlineData(new[] { "collect" }, "A10", null)]
    [InlineData(new[] { "A10", "A4" }, "A4", null)]
    [InlineData(new[] { "collect", "A4" }, "collect", null)]
    public void A_running_json_without_kind_is_a_full_check_only_in_the_exact_shape_a_full_check_writes(string[] actions, string current, RunKind? expected)
    {
        Running(actions, current, RunTrigger.Manual, kind: null).KindOrMarker().Should().Be(expected);
    }

    [Theory]
    [InlineData(RunKind.Collect)]
    [InlineData(RunKind.Act)]
    public void A_running_json_with_kind_is_read_by_its_kind_alone(RunKind kind)
    {
        Running(["A10"], "A10", RunTrigger.Timer, kind).KindOrMarker().Should().Be(kind);
    }

    // ---------- the sweep of a dead holder ----------

    public static TheoryData<string, string[], string, RunTrigger, RunKind?, RunKind?, string[]> DeadHolders => new()
    {
        { "a full check measuring", ["collect"], "collect", RunTrigger.Manual, RunKind.Collect, RunKind.Collect, [] },
        { "the timer's pass", ["A5", "A10"], "A10", RunTrigger.Timer, RunKind.Collect, RunKind.Collect, ["A5", "A10"] },
        { "an act --timer of the same ids", ["A5", "A10"], "A10", RunTrigger.Timer, RunKind.Act, RunKind.Act, ["A5", "A10"] },
        { "an older full check measuring", ["collect"], "collect", RunTrigger.Manual, null, RunKind.Collect, [] },
        { "an older act", ["A10"], "A10", RunTrigger.Manual, null, null, ["A10"] },
    };

    [Theory]
    [MemberData(nameof(DeadHolders))]
    public void A_dead_holder_s_line_takes_its_kind_and_lists_only_actions(string what, string[] actions, string current, RunTrigger trigger, RunKind? kind, RunKind? expected, string[] rows)
    {
        var dead = Running(actions, current, trigger, kind);
        Directory.CreateDirectory(_sandbox.Paths.StateDirectory);
        File.WriteAllText(RunningState.File(_sandbox.Paths), JsonSerializer.Serialize(dead, WslCareJsonContext.Default.RunningFile));

        RunningSweep.Apply(_sandbox.Paths, _sandbox.Files, new FakeProcessTable(), Now, RunningReadRetry.Default, RunId.New(Now, 1), 1).Should().BeOfType<RunningSweep.Clear>(what);

        var line = History().Should().ContainSingle(what).Subject;
        line.Kind.Should().Be(expected, what);
        line.Actions.Select(a => a.Id).Should().Equal(rows, what);
        line.Actions.Select(a => a.Status).Should().AllBe(ActionStatus.Interrupted, what);
    }

    /// <summary>§15o review G4: the sweep's <c>file.Actions</c> is never null — a file without <c>actions</c> is unreadable, so
    /// it blocks and is never swept into a line.</summary>
    [Fact]
    public void A_running_json_without_actions_is_unreadable_and_never_swept_into_a_line()
    {
        Directory.CreateDirectory(_sandbox.Paths.StateDirectory);
        File.WriteAllText(RunningState.File(_sandbox.Paths), """{"schemaVersion":1,"runId":"20261002T115100Z-4321","trigger":"manual","current":"collect","pid":4321,"processStartUtc":"2026-10-02T11:50:00+00:00","startedAt":"2026-10-02T11:51:00+00:00","heartbeatAt":"2026-10-02T11:55:00+00:00"}""");

        RunningSweep.Apply(_sandbox.Paths, _sandbox.Files, new FakeProcessTable(), Now, new RunningReadRetry(0, TimeSpan.Zero, static _ => { }), RunId.New(Now, 1), 1).Should().BeOfType<RunningSweep.Blocked>();

        File.Exists(RunRecordWriter.HistoryFileIn(_sandbox.Paths)).Should().BeFalse("nothing was written for a file that cannot be read");
    }

    // ---------- the request sweep, an act's request ----------

    [Fact]
    public async Task A_swept_act_request_names_its_kind_and_marks_each_asked_action_interrupted()
    {
        var request = new RunRequestFile(1, RunId.New(Now.AddMinutes(-30), 81), "act", ["A10", "A4"], RunTrigger.Manual, Now.AddMinutes(-30));
        RunRequests.Create(_sandbox.Paths, _sandbox.Files, request).Should().BeOfType<ExclusiveCreate.Created>();
        var runner = new RecordingCommandRunner().Script(UnitCommands.Show(request.RunId).Argv, 0, "ActiveState=inactive\nJob=\n");

        await RequestSweep.ApplyAsync(_sandbox.Paths, _sandbox.Files, runner, new FakeProcessTable(), Now, own: null);

        var line = History().Should().ContainSingle().Subject;
        line.Kind.Should().Be(RunKind.Act);
        line.Actions.Select(a => (a.Id, a.Status)).Should().Equal(("A10", ActionStatus.Interrupted), ("A4", ActionStatus.Interrupted));
    }

    /// <summary>§15o review G2: a request kind this build does not know is no kind at all — never filed as an act, and its
    /// "actions" are not taken for action ids (the reader refuses such a request, but the mapping must not lean on that).</summary>
    [Theory]
    [InlineData("archive", new[] { "A13" })]
    [InlineData("Collect", new[] { "collect" })]
    [InlineData("", new[] { "collect" })]
    public void A_request_of_an_unknown_kind_gets_a_line_with_no_kind_and_no_action_rows(string kind, string[] actions)
    {
        var request = new RunRequestFile(1, RunId.New(Now, 82), kind, actions, RunTrigger.Manual, Now);

        var line = request.TerminalLine(Now, Now, RunOutcome.Refused, ActionStatus.Refused, "busy: a test");

        line.Kind.Should().BeNull($"\"{kind}\" is neither act nor collect");
        line.Actions.Should().BeEmpty("rows of an unknown kind would be guesses");
    }

    [Theory]
    [InlineData("act", RunKind.Act)]
    [InlineData("collect", RunKind.Collect)]
    public void A_request_of_a_known_kind_gets_that_kind(string kind, RunKind expected)
    {
        var request = new RunRequestFile(1, RunId.New(Now, 83), kind, kind == "act" ? ["A10"] : ["collect"], RunTrigger.Manual, Now);

        request.TerminalLine(Now, Now, RunOutcome.Refused, ActionStatus.Refused, "busy: a test").Kind.Should().Be(expected);
    }

    // ---------- the reconcile ----------

    /// <summary>§15o review G1: a detail kind this build does not know (a future one) is no kind — never a full check.</summary>
    [Theory]
    [InlineData("act", RunKind.Act)]
    [InlineData(null, RunKind.Collect)]
    [InlineData("archive", null)]
    [InlineData("Act", null)]
    public void A_reconciled_orphan_takes_its_kind_from_its_detail(string? detailKind, RunKind? expected)
    {
        var runId = RunId.New(Now, 95);
        var path = RunDetailStore.Absolute(_sandbox.Paths, RunDetailStore.RelativePath(runId));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new RunDetailHead(1, runId, RunTrigger.Manual, Now, Now, false) { Kind = detailKind }, WslCareJsonContext.Default.RunDetailHead));

        RunReconcile.Apply(_sandbox.Paths, _sandbox.Files, Now);

        History().Should().ContainSingle().Which.Kind.Should().Be(expected);
    }

    // ---------- reading ----------

    [Fact]
    public void A_line_written_before_kind_still_parses_and_answers_no_kind()
    {
        Append("""{"schemaVersion":1,"runId":"20261002T120000Z-5","trigger":"manual","startedAt":"2026-10-02T12:00:00+00:00","endedAt":"2026-10-02T12:01:00+00:00","outcome":"completed","actions":[]}""");

        var read = RunHistory.Read(_sandbox.Paths, _sandbox.Files);

        read.Unparseable.Should().Be(0);
        read.Records.Single().Kind.Should().BeNull();
        RunLogs.Runs(_sandbox.Paths, _sandbox.Files, Period()).Runs.Single().Kind.Should().BeNull("absent, never guessed");
    }

    /// <summary>The residual plan §15o states: a kind this build does not know makes the line unparseable — counted, never a crash
    /// (reachable only after a downgrade, as an unknown outcome is).</summary>
    [Fact]
    public void A_line_with_a_kind_this_build_does_not_know_is_counted_unparseable()
    {
        Append("""{"schemaVersion":1,"runId":"20261002T120000Z-6","trigger":"manual","startedAt":"2026-10-02T12:00:00+00:00","endedAt":"2026-10-02T12:01:00+00:00","outcome":"completed","actions":[],"kind":"archive"}""");

        var read = RunHistory.Read(_sandbox.Paths, _sandbox.Files);

        read.Unparseable.Should().Be(1);
        read.Records.Should().BeEmpty();
    }

    [Fact]
    public void Runs_answer_each_line_s_kind()
    {
        var writer = new RunRecordWriter(_sandbox.Paths, _sandbox.Files);
        writer.Append(new RunRecord(1, RunId.New(Now, 7), RunTrigger.Manual, Now, Now, RunOutcome.Completed, [], RunKind.Collect));
        writer.Append(new RunRecord(1, RunId.New(Now, 8), RunTrigger.Manual, Now, Now, RunOutcome.Completed, [new ActionRecord("A10", 1, 5) { Status = ActionStatus.Ran }], RunKind.Act));

        RunLogs.Runs(_sandbox.Paths, _sandbox.Files, Period()).Runs.Select(r => r.Kind).Should().Equal("collect", "act");
    }

    /// <summary>Plan §15o: the pseudo-row used to put a zero <c>collect</c> entry into <c>perAction</c> for every refused or swept
    /// full check; a refused full check's line now carries none.</summary>
    [Fact]
    public void A_refused_full_check_adds_no_collect_entry_to_the_per_action_totals()
    {
        var request = new RunRequestFile(1, RunId.New(Now, 9), RunKinds.FullCheckName, [RunKinds.FullCheckName], RunTrigger.Manual, Now);
        new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(request.TerminalLine(Now, Now, RunOutcome.Refused, ActionStatus.Refused, "busy: a test"));

        var logs = RunLogs.Logs(_sandbox.Paths, _sandbox.Files, Period(), action: null);

        logs.Runs.Total.Should().Be(1, "the refused run is counted as a run");
        logs.PerAction.Should().BeEmpty("a full check is not an action");
    }

    // ---------- the reserved name ----------

    [Fact]
    public void No_action_may_carry_the_full_check_s_reserved_name()
    {
        ActionId.All.Select(id => id.Text).Should().NotContain(t => string.Equals(t, RunKinds.FullCheckName, StringComparison.OrdinalIgnoreCase));
        ActionId.Find(RunKinds.FullCheckName).Should().BeNull();
        ActionId.All.Should().NotBeEmpty("the check reads the real registry");
    }

    private void Append(string json)
    {
        Directory.CreateDirectory(_sandbox.Paths.StateDirectory);
        File.AppendAllText(RunRecordWriter.HistoryFileIn(_sandbox.Paths), json + "\n");
    }

    private static LogPeriod Period() => ((PeriodParse.Parsed)LogPeriod.Parse("2026-10-02", Now)).Period;
}
