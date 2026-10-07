using System.Text.Json;
using System.Text.Json.Nodes;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.History;
using WslCare.Core.Json;
using WslCare.Core.Files;
using WslCare.Core.Records;
using WslCare.Core.Systemd;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Records;

/// <summary>
/// The PR #16 retro round (plan §15o, 2026-10-06): a <c>kind</c> this build does not know — written by a NEWER build, met
/// after a downgrade — is read as UNKNOWN, never guessed into <c>collect</c> or <c>act</c>, and never makes the record it sits
/// in unreadable. Three states, kept apart: the member is ABSENT (an older writer: the legacy inference applies), the member
/// holds a KNOWN name, or the member holds anything else (unknown: no inference, never written back). Every fixture is raw
/// JSON, so each test states the bytes on disk, not this build's reading of them.
/// </summary>
public sealed class RunKindDowngradeTests : IDisposable
{
    private const int DeadPid = 4321;
    private static readonly DateTimeOffset Now = FixedTimeProvider.DefaultNow;
    private static readonly RunningReadRetry NoWait = new(0, TimeSpan.Zero, static _ => { });
    private static readonly RunId DeadRun = RunId.TryParse("20261002T115100Z-4321")!;

    private readonly SandboxHost _sandbox = new("run-kind-downgrade");

    public void Dispose() => _sandbox.Dispose();

    // ---------- running.json ----------

    /// <summary>The consultant's point (consultation 0d924598): two files identical but for <c>kind</c>, both in the exact shape
    /// an older full check wrote (<c>["collect"]</c> + current <c>collect</c>). Only the file WITHOUT the member may be inferred a
    /// full check; a member holding a value this build does not know — a name, an explicit null, an integer — is unknown, and
    /// mapping it to "no kind" before the inference would file it as <c>collect</c>.</summary>
    [Theory]
    [InlineData(null, "collect")]
    [InlineData("\"archive\"", null)]
    [InlineData("null", null)]
    [InlineData("0", null)]
    public void An_older_running_json_is_inferred_a_full_check_only_when_its_kind_is_missing_never_when_it_is_unknown(string? kindJson, string? lineKind)
    {
        PlantRunning(kindJson, heartbeatAgo: TimeSpan.FromMinutes(5));

        var sweep = RunningSweep.Apply(_sandbox.Paths, _sandbox.Files, new FakeProcessTable(), Now, NoWait, RunId.New(Now, 1), 1);

        sweep.Should().BeOfType<RunningSweep.Clear>($"a dead holder whose kind is {kindJson ?? "missing"} is swept, never a state that refuses every run");
        var line = HistoryLines().Should().ContainSingle().Subject;
        KindOf(line).Should().Be(lineKind, $"kind {kindJson ?? "missing"}: only a MISSING member is inferred, an unknown one is never written back as a guess (line: {line.GetRawText()})");
        File.Exists(RunningState.File(_sandbox.Paths)).Should().BeFalse("the swept file goes");
    }

    [Theory]
    [InlineData("\"archive\"")]
    [InlineData("1")]
    public void A_running_json_with_a_kind_this_build_does_not_know_is_still_read_and_judged(string kindJson)
    {
        PlantRunning(kindJson, heartbeatAgo: TimeSpan.FromSeconds(2));
        var processes = new FakeProcessTable().Alive(DeadPid, Now.AddMinutes(-10));

        var status = RunningState.Read(_sandbox.Paths, _sandbox.Files, processes, Now, NoWait);

        status.Should().BeOfType<RunningStatus.Live>("a newer build's live run is a live run - an unreadable state would refuse every act and timer pass until the file is removed by hand");
    }

    // ---------- a history line ----------

    [Theory]
    [InlineData("\"archive\"")]
    [InlineData("null")]
    [InlineData("0")]
    public void A_history_line_with_a_kind_this_build_does_not_know_still_parses(string kindJson)
    {
        AppendLine(LineJson(DeadRun, kindJson));

        var read = RunHistory.Read(_sandbox.Paths, _sandbox.Files);

        read.Unparseable.Should().Be(0, "an unknown kind is a fact about the line, not a broken line");
        read.Records.Should().ContainSingle().Which.RunId.Should().Be(DeadRun);
    }

    /// <summary>Gate G2: an integer is never a kind's name — <c>0</c> used to be read as <c>collect</c> (the enum's ordinal).</summary>
    [Fact]
    public void An_integer_kind_on_a_history_line_is_never_read_as_a_name()
    {
        AppendLine(LineJson(DeadRun, "0"));

        RunLogs.Runs(_sandbox.Paths, _sandbox.Files, Period()).Runs.Should().ContainSingle()
            .Which.Kind.Should().Be("0", "the value is answered as written - an integer is not the ordinal of collect");
    }

    /// <summary>Gate G2: <c>RunTrigger</c> and <c>RunOutcome</c> take no integers — an integer is refused like any value they do
    /// not know (their tolerance to an unknown value is an owner question, plan §15o retro round).</summary>
    [Theory]
    [InlineData("trigger", "0")]
    [InlineData("outcome", "0")]
    [InlineData("trigger", "2")]
    [InlineData("outcome", "4")]
    public void An_integer_trigger_or_outcome_makes_the_line_unparseable_like_any_unknown_value(string member, string value)
    {
        var line = JsonNode.Parse(LineJson(DeadRun, kindJson: null))!.AsObject();
        line[member] = JsonNode.Parse(value);
        AppendLine(line.ToJsonString());

        var read = RunHistory.Read(_sandbox.Paths, _sandbox.Files);

        read.Unparseable.Should().Be(1, $"\"{member}\": {value} is no name of a {member}");
        read.Records.Should().BeEmpty();
    }

    [Fact]
    public void An_integer_trigger_in_running_json_makes_it_unreadable()
    {
        PlantRunning(kindJson: "\"act\"", heartbeatAgo: TimeSpan.FromSeconds(2), trigger: "1");

        RunningState.Read(_sandbox.Paths, _sandbox.Files, new FakeProcessTable().Alive(DeadPid, Now.AddMinutes(-10)), Now, NoWait)
            .Should().BeOfType<RunningStatus.Unreadable>("\"trigger\": 1 is no trigger's name");
    }

    // ---------- the history-first checks see the line ----------

    [Fact]
    public void The_sweep_of_a_dead_holder_sees_its_unknown_kind_line_and_writes_no_second_one()
    {
        AppendLine(LineJson(DeadRun, "\"archive\""));
        PlantRunning(kindJson: "\"archive\"", heartbeatAgo: TimeSpan.FromMinutes(5));

        RunningSweep.Apply(_sandbox.Paths, _sandbox.Files, new FakeProcessTable(), Now, NoWait, RunId.New(Now, 1), 1).Should().BeOfType<RunningSweep.Clear>();

        HistoryLines().Should().ContainSingle("the run had already recorded itself - a second terminal line would contradict it");
        File.Exists(RunningState.File(_sandbox.Paths)).Should().BeFalse();
    }

    [Fact]
    public async Task The_request_sweep_sees_an_unknown_kind_line_and_only_removes_the_request()
    {
        var request = new RunRequestFile(1, DeadRun, "act", ["A10"], RunTrigger.Manual, Now.AddMinutes(-30));
        RunRequests.Create(_sandbox.Paths, _sandbox.Files, request).Should().BeOfType<ExclusiveCreate.Created>();
        AppendLine(LineJson(DeadRun, "\"archive\""));
        var runner = new RecordingCommandRunner().Script(UnitCommands.Show(DeadRun).Argv, 0, "ActiveState=inactive\nJob=\n");

        await RequestSweep.ApplyAsync(_sandbox.Paths, _sandbox.Files, runner, new FakeProcessTable(), Now, own: null);

        HistoryLines().Should().ContainSingle("the run recorded itself; its request only loses its file");
        RunRequests.Find(_sandbox.Paths, _sandbox.Files, DeadRun).Should().BeNull("the request of a recorded run is removed");
    }

    [Fact]
    public void The_reconcile_sees_an_unknown_kind_line_and_files_its_detail_as_no_orphan()
    {
        StageDetail(DeadRun, """{"schemaVersion":1,"runId":"20261002T115100Z-4321","trigger":"manual","startedAt":"2026-10-02T11:51:00+00:00","endedAt":"2026-10-02T11:52:00+00:00","dryRun":false,"kind":"archive"}""");
        AppendLine(LineJson(DeadRun, "\"archive\""));

        var report = RunReconcile.Apply(_sandbox.Paths, _sandbox.Files, Now);

        report.Interrupted.Should().BeEmpty("the detail's run has its line");
        HistoryLines().Should().ContainSingle();
    }

    // ---------- the reconcile reads a detail's kind: absent / act / unknown ----------

    /// <summary>Gate G0 + consultant: only a detail WITHOUT the member is a full run's; an explicit <c>null</c> binds like a
    /// missing member in a <c>string?</c> and was filed as <c>collect</c>. Every fixture here is a readable detail, so each line
    /// carries the reconcile's readable reason.</summary>
    [Theory]
    [InlineData(null, "collect")]
    [InlineData("\"act\"", "act")]
    [InlineData("null", null)]
    [InlineData("\"\"", null)]
    [InlineData("\"archive\"", null)]
    [InlineData("\"collect\"", null)]
    [InlineData("\"Act\"", null)]
    [InlineData("0", null)]
    public void A_reconciled_orphan_s_kind_comes_from_its_detail_s_kind_member_absent_act_or_unknown(string? kindJson, string? lineKind)
    {
        var detail = JsonNode.Parse("""{"schemaVersion":1,"runId":"20261002T115100Z-4321","trigger":"manual","startedAt":"2026-10-02T11:51:00+00:00","endedAt":"2026-10-02T11:52:00+00:00","dryRun":false}""")!.AsObject();
        if (kindJson is not null)
        {
            detail["kind"] = JsonNode.Parse(kindJson);
        }

        StageDetail(DeadRun, detail.ToJsonString());

        RunReconcile.Apply(_sandbox.Paths, _sandbox.Files, Now).Interrupted.Should().Equal(DeadRun.Text);

        var line = HistoryLines().Should().ContainSingle().Subject;
        KindOf(line).Should().Be(lineKind, $"detail kind {kindJson ?? "missing"} (line: {line.GetRawText()})");
        line.GetProperty("reason").GetString().Should().Be(RunReconcile.InterruptedReason, "the detail is readable - only its kind is unknown");
    }

    // ---------- runs show / logs never read an unknown detail as a full run ----------

    [Fact]
    public void Runs_show_answers_a_detail_of_an_unknown_kind_with_what_is_known_never_as_a_full_run()
    {
        var at = Now.AddHours(-1);
        var id = RunId.New(at, 31);
        StageDetail(id, ArchiveDetail(id, at));
        new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(new RunRecord(1, id, RunTrigger.Manual, at, at, RunOutcome.Completed, [new ActionRecord("A10", 2, 2_000) { Status = ActionStatus.Ran }], RunKind.Act) { Detail = RunDetailStore.RelativePath(id) });

        var show = RunShow.Read(_sandbox.Paths, _sandbox.Files, new FakeProcessTable(), Now, NoWait, id);

        show.State.Should().Be(RunShowState.Done, "the history line decides the state");
        show.Run!.RunId.Should().Be(id.Text, "the line is answered");
        show.Detail.Should().BeNull("a detail of a kind this build does not read is never shown as a full run's (it was: kind collect, its timer pass's actions)");
        show.DetailState.Should().Be("unreadable", "this build cannot read it");
        show.DetailProblem.Should().Be(RunShow.UnknownKind(RecordedKind.Unknown("archive")), "it says WHY the detail is not shown");
        show.DetailProblem.Should().Contain("\"archive\"").And.Contain("does not read");
    }

    [Fact]
    public void Logs_never_read_timer_pass_outcomes_from_a_detail_of_an_unknown_kind()
    {
        var at = Now.AddHours(-1);
        var id = RunId.New(at, 32);
        StageDetail(id, ArchiveDetail(id, at));
        new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(new RunRecord(1, id, RunTrigger.Manual, at, at, RunOutcome.Completed, [new ActionRecord("A10", 2, 2_000) { Status = ActionStatus.Ran }], RunKind.Act) { Detail = RunDetailStore.RelativePath(id) });

        var cleanup = RunLogs.Logs(_sandbox.Paths, _sandbox.Files, Period(), action: null, detail: true).Cleanups.Should().ContainSingle().Subject;

        cleanup.FreedBytes.Should().Be(2_000, "the history line's figures still count");
        cleanup.Removed.Should().BeEmpty("the objects of a detail this build cannot read are not guessed from its timer pass");
        cleanup.DetailState.Should().Be("unreadable");
    }

    /// <summary>PR #44 gate round, plan round: a detail logs could not read — of a kind this build does not read, or one that
    /// does not parse — is not a detail read with nothing in it ("absent is not zero"): it counts as NOT read, and its cleanups
    /// say <c>unreadable</c>, as <c>runs show</c> does.</summary>
    [Theory]
    [InlineData("archive")]
    [InlineData("not json")]
    public void Logs_count_a_detail_they_could_not_read_as_not_read_never_as_read(string what)
    {
        var at = Now.AddHours(-1);
        var id = RunId.New(at, 33);
        StageDetail(id, what == "archive" ? ArchiveDetail(id, at) : "{ this is not json");
        new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(new RunRecord(1, id, RunTrigger.Manual, at, at, RunOutcome.Completed, [new ActionRecord("A10", 2, 2_000) { Status = ActionStatus.Ran }], RunKind.Act) { Detail = RunDetailStore.RelativePath(id) });

        var logs = RunLogs.Logs(_sandbox.Paths, _sandbox.Files, Period(), action: null, detail: true);

        logs.DetailsRead.Should().Be(0, $"a detail that is {what} was opened but not read");
        logs.DetailsNotRead.Should().Be(1);
        logs.Cleanups.Should().ContainSingle().Which.DetailState.Should().Be("unreadable");
    }

    /// <summary>PR #44 gate round, code round: every kind-less line carries a reason the contract's prefixes mark — a dead holder
    /// whose kind is not known included — so a reader that falls back to the prefixes never takes it for a full check.</summary>
    [Theory]
    [InlineData("\"archive\"")]
    [InlineData("null")]
    [InlineData("0")]
    public void The_swept_line_of_a_holder_whose_kind_is_not_known_is_marked_not_a_full_check(string kindJson)
    {
        PlantRunning(kindJson, heartbeatAgo: TimeSpan.FromMinutes(5));

        RunningSweep.Apply(_sandbox.Paths, _sandbox.Files, new FakeProcessTable(), Now, NoWait, RunId.New(Now, 1), 1).Should().BeOfType<RunningSweep.Clear>();

        var line = HistoryLines().Should().ContainSingle().Subject;
        KindOf(line).Should().BeNull();
        HistoryReasons.MarksNotAFullCheck(line.GetProperty("reason").GetString()!).Should().BeTrue($"a kind-less line must be told by its reason ({line.GetRawText()})");
    }

    /// <summary>PR #44 gate round, code round: every <see cref="RunKind"/> has its own name, and a member nobody mapped is a
    /// defect that throws — never silently <c>collect</c>.</summary>
    [Fact]
    public void Every_run_kind_has_its_own_name_and_an_unmapped_member_throws()
    {
        var names = Enum.GetValues<RunKind>().Select(RunKinds.Name).ToList();

        names.Should().OnlyHaveUniqueItems().And.HaveCount(Enum.GetValues<RunKind>().Length);
        Enum.GetValues<RunKind>().Should().OnlyContain(k => RunKinds.Known(RunKinds.Name(k)) == k, "each name reads back as its kind");
        FluentActions.Invoking(() => RunKinds.Name((RunKind)99)).Should().Throw<ArgumentOutOfRangeException>("an unmapped member is never filed as collect");
    }

    /// <summary>PR #44 gate round, code round: an absent or unknown kind has no known kind — reading one is refused, never the
    /// enum's default (<c>collect</c>).</summary>
    [Fact]
    public void The_known_kind_of_an_absent_or_unknown_kind_is_refused_never_collect()
    {
        RecordedKind.Act.Known.Should().Be(RunKind.Act);
        FluentActions.Invoking(() => RecordedKind.Absent.Known).Should().Throw<InvalidOperationException>("absent is not collect");
        FluentActions.Invoking(() => RecordedKind.Unknown("archive").Known).Should().Throw<InvalidOperationException>("unknown is not collect");
    }

    // ---------- the wire type itself ----------

    [Theory]
    [InlineData("\"collect\"", "collect")]
    [InlineData("\"act\"", "act")]
    [InlineData("\"archive\"", "unknown \"archive\"")]
    [InlineData("\"Collect\"", "unknown \"Collect\"")]
    [InlineData("null", "unknown \"null\"")]
    [InlineData("0", "unknown \"0\"")]
    [InlineData("true", "unknown \"true\"")]
    [InlineData("{\"a\":1}", "unknown \"{…}\"")]
    [InlineData("[1,2]", "unknown \"[…]\"")]
    public void Every_json_value_of_kind_reads_as_a_known_name_or_as_unknown_never_as_a_failure(string kindJson, string expected)
    {
        var view = JsonSerializer.Deserialize($$"""{"kind":{{kindJson}},"after":1}""", WslCareJsonContext.Default.DetailKindView)!;

        view.Kind.ToString().Should().Be(expected);
    }

    [Fact]
    public void A_missing_kind_reads_as_absent_and_a_known_one_round_trips()
    {
        JsonSerializer.Deserialize("""{}""", WslCareJsonContext.Default.DetailKindView)!.Kind.IsAbsent.Should().BeTrue();

        var line = new RunRecord(1, DeadRun, RunTrigger.Manual, Now, Now, RunOutcome.Completed, [], RecordedKind.Act);
        var json = JsonSerializer.Serialize(line, WslCareJsonContext.Compact.RunRecord);
        json.Should().Contain("\"kind\":\"act\"");
        RunHistory.ParseLine(json).Single().Kind.Should().Be(RecordedKind.Act);
        JsonSerializer.Serialize(line with { Kind = RecordedKind.Absent }, WslCareJsonContext.Compact.RunRecord).Should().NotContain("\"kind\"", "absent is not written");
    }

    /// <summary>A writer never writes back an unknown kind as if it knew it: the converter refuses, and every writer that carries a
    /// read kind forward goes through <see cref="RecordedKind.ForWriting"/>.</summary>
    [Fact]
    public void An_unknown_kind_is_never_written()
    {
        var line = new RunRecord(1, DeadRun, RunTrigger.Manual, Now, Now, RunOutcome.Completed, [], RecordedKind.Unknown("archive"));

        FluentActions.Invoking(() => JsonSerializer.Serialize(line, WslCareJsonContext.Compact.RunRecord)).Should().Throw<InvalidOperationException>().WithMessage("*only when it is known*");
        RecordedKind.Unknown("archive").ForWriting.IsAbsent.Should().BeTrue();
        RecordedKind.Collect.ForWriting.Should().Be(RecordedKind.Collect);
    }

    // ---------- fixtures ----------

    /// <summary>A detail a newer build might write: <c>kind: "archive"</c> beside a <c>timerPass</c> that WOULD read as a full
    /// run's outcomes, so a reader that guesses "full run" shows them.</summary>
    private static string ArchiveDetail(RunId id, DateTimeOffset at)
    {
        var removed = new ActionRun(2, 2_000, "measured", 9_000, 7_000, [new ActionItem("file", "a", 1_000), new ActionItem("file", "b", 1_000)], [], "removed two")
        {
            NotRemoved = [],
            Notes = [],
        };
        var pass = new TimerPass(true, string.Empty, false, string.Empty, null, [new ActionOutcome("A10", "A10 summary", ActionStatus.Ran, "ran", null, removed)], [], RunOutcome.Completed);
        var detail = JsonSerializer.SerializeToNode(new TimerPassView(pass), WslCareJsonContext.Default.TimerPassView)!.AsObject();
        detail["schemaVersion"] = 1;
        detail["runId"] = id.Text;
        detail["trigger"] = "manual";
        detail["startedAt"] = at;
        detail["endedAt"] = at;
        detail["dryRun"] = false;
        detail["kind"] = "archive";
        return detail.ToJsonString();
    }

    private void PlantRunning(string? kindJson, TimeSpan heartbeatAgo, string trigger = "\"manual\"")
    {
        var file = JsonNode.Parse("""{"schemaVersion":1,"runId":"20261002T115100Z-4321","actions":["collect"],"current":"collect","pid":4321,"processStartUtc":"2026-10-02T11:50:00+00:00","startedAt":"2026-10-02T11:51:00+00:00"}""")!.AsObject();
        file["trigger"] = JsonNode.Parse(trigger);
        file["heartbeatAt"] = Now - heartbeatAgo;
        if (kindJson is not null)
        {
            file["kind"] = JsonNode.Parse(kindJson);
        }

        Directory.CreateDirectory(_sandbox.Paths.StateDirectory);
        File.WriteAllText(RunningState.File(_sandbox.Paths), file.ToJsonString());
    }

    private static string LineJson(RunId runId, string? kindJson)
    {
        var line = JsonNode.Parse("""{"schemaVersion":1,"trigger":"manual","startedAt":"2026-10-02T11:51:00+00:00","endedAt":"2026-10-02T11:52:00+00:00","outcome":"completed","actions":[]}""")!.AsObject();
        line["runId"] = runId.Text;
        if (kindJson is not null)
        {
            line["kind"] = JsonNode.Parse(kindJson);
        }

        return line.ToJsonString();
    }

    private void StageDetail(RunId runId, string json)
    {
        var path = RunDetailStore.Absolute(_sandbox.Paths, RunDetailStore.RelativePath(runId));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private void AppendLine(string json)
    {
        Directory.CreateDirectory(_sandbox.Paths.StateDirectory);
        File.AppendAllText(RunRecordWriter.HistoryFileIn(_sandbox.Paths), json + "\n");
    }

    private IReadOnlyList<JsonElement> HistoryLines()
    {
        var path = RunRecordWriter.HistoryFileIn(_sandbox.Paths);
        return File.Exists(path)
            ? [.. File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement.Clone())]
            : [];
    }

    private static string? KindOf(JsonElement line) =>
        line.TryGetProperty("kind", out var kind) ? kind.GetString() : null;

    private static LogPeriod Period() => ((PeriodParse.Parsed)LogPeriod.Parse("2026-10-02", Now)).Period;
}
