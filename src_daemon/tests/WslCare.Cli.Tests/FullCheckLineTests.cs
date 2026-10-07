using System.Text.Json;

using FluentAssertions;

using WslCare.Cli.Commands;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Json;
using WslCare.Core.Records;
using WslCare.Core.Systemd;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// Plan §15o: a full check's history line names itself by ONE rule — <c>kind: "collect"</c> — whatever ended it, its
/// <c>actions</c> hold per-action results only (never a <c>collect</c> row), and its reason never looks like one of the lines a
/// reader of a KIND-LESS line must not take for a full check (<see cref="HistoryReasons"/>, <c>contracts/history-reasons.json</c>
/// — coai plan round #1). Every writer of a full check's terminal line is driven here: <see cref="Ending"/> is that list. The
/// lines are read at the WIRE — <c>history.jsonl</c> as raw JSON — so the test says what a reader actually sees.
/// </summary>
public sealed class FullCheckLineTests : IDisposable
{
    private static readonly DateTimeOffset Now = DetachedRunHarness.Now;

    /// <summary>What each ending's line must say — proof the staging reached the writer it names, not a neighbour's path.</summary>
    private static readonly Dictionary<Ending, (string Outcome, string Reason)> Expected = new()
    {
        [Ending.Completed] = ("completed", string.Empty),
        [Ending.ObserveOnly] = ("observeOnly", string.Empty),
        [Ending.FailedOnItsDetail] = ("failed", "the run detail could not be written"),
        [Ending.RefusedAtTheLock] = ("refused", "another run is in progress"),
        [Ending.CutOffDuringTheMeasurement] = ("interrupted", "during the measurement"),
        [Ending.CutOffWhileSweeping] = ("interrupted", "while it swept the request folder"),
        [Ending.CutOffBeforeItStarted] = ("interrupted", "cut off before it started"),
        [Ending.SweptRequest] = ("interrupted", "swept: the detached run never recorded itself"),
        [Ending.SweptHolder] = ("interrupted", "swept: "),
        [Ending.SweptHolderAfterAStop] = ("interrupted", "stopped: a stop was requested at"),
    };

    private readonly DetachedRunHarness _harness = new("full-check-line");
    private readonly Dictionary<Ending, Func<RunRequestFile, RunId>> _endings;

    public FullCheckLineTests()
    {
        _endings = new()
        {
            [Ending.Completed] = request => Ran(request, _harness.Host(), ExitCode.Ok),
            [Ending.ObserveOnly] = ObserveOnly,
            [Ending.FailedOnItsDetail] = request => Ran(request, _harness.Host(files: new RefusingDetailWrites(_harness.Sandbox.Files)), ExitCode.RunFailed),
            [Ending.RefusedAtTheLock] = RefusedAtTheLock,
            [Ending.CutOffDuringTheMeasurement] = request => CutOffBy(argv => argv.SequenceEqual(SystemdCommands.JournalDiskUsage.Argv), request),
            [Ending.CutOffWhileSweeping] = CutOffWhileSweeping,
            [Ending.CutOffBeforeItStarted] = CutOffBeforeItStarted,
            [Ending.SweptRequest] = SweptRequest,
            [Ending.SweptHolder] = _ => SweptHolder(marked: false),
            [Ending.SweptHolderAfterAStop] = _ => SweptHolder(marked: true),
        };
    }

    /// <summary>Every way a full check's terminal line is written — one per writer and outcome (plan §15o, decision 2).</summary>
    public enum Ending
    {
        /// <summary><c>CollectRun.Line</c>, completed.</summary>
        Completed,

        /// <summary><c>CollectRun.Line</c> under an invalid configuration layer.</summary>
        ObserveOnly,

        /// <summary><c>CollectRun.Line</c> when its detail could not be written.</summary>
        FailedOnItsDetail,

        /// <summary><c>DetachedRuns.Refused</c>: the lock was held.</summary>
        RefusedAtTheLock,

        /// <summary><c>CollectRun.RecordCutOff</c> during the measurement.</summary>
        CutOffDuringTheMeasurement,

        /// <summary><c>CollectRun.RecordCutOff</c> while it swept the request folder.</summary>
        CutOffWhileSweeping,

        /// <summary><c>DetachedRuns.CutOff</c>'s line for a full check — a shape no path reaches from outside (<c>CollectRun</c>
        /// records its own cut-offs first), built by the very expression <c>CutOff</c> appends; the reachable path is the act's,
        /// <see cref="An_act_request_cut_off_inside_its_request_sweep_gets_ONE_act_line_from_DetachedRuns_CutOff"/>.</summary>
        CutOffBeforeItStarted,

        /// <summary><c>RequestSweep.Interrupted</c>: its unit is gone.</summary>
        SweptRequest,

        /// <summary><c>RunningSweep.SweepDead</c>: a dead holder of <c>running.json</c>.</summary>
        SweptHolder,

        /// <summary><c>RunningSweep.SweepDead</c> after <c>act --stop</c> marked it.</summary>
        SweptHolderAfterAStop,
    }

    public static TheoryData<Ending> Endings => [.. Enum.GetValues<Ending>()];

    public void Dispose() => _harness.Dispose();

    [Theory]
    [MemberData(nameof(Endings))]
    public void Every_terminal_line_of_a_detached_full_check_names_it_by_kind_and_carries_no_collect_row(Ending ending)
    {
        var line = LineOf(End(ending));

        line.GetProperty("outcome").GetString().Should().Be(Expected[ending].Outcome, line.GetRawText());
        var reason = Reason(line);
        (Expected[ending].Reason.Length == 0 ? reason.Length == 0 : reason.Contains(Expected[ending].Reason, StringComparison.Ordinal))
            .Should().BeTrue($"a {ending} line says \"{Expected[ending].Reason}\" (line: {line.GetRawText()})");
        Kind(line).Should().Be("collect", $"a full check {ending} is told by kind alone (line: {line.GetRawText()})");
        ActionIds(line).Should().NotContain(RunKinds.FullCheckName, $"actions hold per-action results, never the full check itself (line: {line.GetRawText()})");
    }

    /// <summary>Plan round #1: dropping the <c>collect</c> row is safe for a reader that still matches by reasons only if no full
    /// check's reason looks like the kind-less lines it excludes.</summary>
    [Theory]
    [MemberData(nameof(Endings))]
    public void No_full_check_s_reason_starts_like_a_line_that_is_not_a_full_check(Ending ending)
    {
        var reason = Reason(LineOf(End(ending)));

        HistoryReasons.MarksNotAFullCheck(reason).Should().BeFalse($"the reason \"{reason}\" of a full check {ending} must start with none of {string.Join(" | ", HistoryReasons.NotAFullCheckWithoutKind.Select(p => p.Prefix))}");
    }

    /// <summary>§15o review O3: the path <c>DetachedRuns.CutOff</c> really takes — an ACT request cut off by a signal inside its
    /// request sweep, before the engine wrote anything — gets ONE line, its kind <c>act</c> and its asked ids interrupted.</summary>
    [Fact]
    public void An_act_request_cut_off_inside_its_request_sweep_gets_ONE_act_line_from_DetachedRuns_CutOff()
    {
        var request = _harness.Plant("act", ["A10", "A9"], TimeSpan.FromSeconds(5), pid: 71);

        CutOffWhileSweeping(request);

        var line = LineOf(request.RunId);
        Kind(line).Should().Be("act");
        Reason(line).Should().EndWith("before it recorded anything (cut off before it started)", "DetachedRuns.CutOff wrote it");
        line.GetProperty("actions").EnumerateArray().Select(a => (a.GetProperty("id").GetString(), a.GetProperty("status").GetString()))
            .Should().Equal(("A10", ActionStatus.Interrupted), ("A9", ActionStatus.Interrupted));
        HistoryReasons.MarksNotAFullCheck(Reason(line)).Should().BeFalse();
    }

    /// <summary>The companion: the prefixes DO mark the kind-less lines they exist for — a list that matched nothing would pass
    /// the test above forever. A reconciled orphan whose detail reads carries its kind, and its reason still starts with the
    /// reconcile's prefix: the contract applies to lines WITHOUT a kind, a line with one is told by its kind.</summary>
    [Fact]
    public void The_prefixes_mark_the_unusable_request_and_the_reconciled_orphan_lines_they_exist_for()
    {
        RequestSweep.Unusable(_harness.Sandbox.Paths, _harness.Sandbox.Files, Now, RunId.New(Now, 91), "not JSON (test)");
        StageOrphan(RunId.New(Now, 92), "{}");
        StageOrphan(RunId.New(Now, 93), JsonSerializer.Serialize(new RunDetailHead(1, RunId.New(Now, 93), RunTrigger.Manual, Now, Now, false), WslCareJsonContext.Default.RunDetailHead));
        RunReconcile.Apply(_harness.Sandbox.Paths, _harness.Sandbox.Files, Now);

        var unusable = LineOf(RunId.New(Now, 91));
        var unreadable = LineOf(RunId.New(Now, 92));
        var orphan = LineOf(RunId.New(Now, 93));
        foreach (var kindless in new[] { unusable, unreadable })
        {
            Kind(kindless).Should().BeEmpty("its kind cannot be known");
            HistoryReasons.MarksNotAFullCheck(Reason(kindless)).Should().BeTrue(kindless.GetRawText());
        }

        Kind(orphan).Should().Be("collect", "a full run's detail carries no kind of its own");
        HistoryReasons.MarksNotAFullCheck(Reason(orphan)).Should().BeTrue("kind first: the prefix is only the fallback");

        // §15o review O4, the stable direction: the reasons AS WRITTEN start with the prefixes the follower matches today.
        Reason(unusable).Should().StartWith(FollowerPrefixes[0]);
        Reason(orphan).Should().StartWith(FollowerPrefixes[1]);
        Reason(unreadable).Should().StartWith(FollowerPrefixes[2]);
    }

    /// <summary>The prefixes the extension's follower excludes a kind-less line by, copied as of 2026-10-05 from
    /// <c>src_vs_code/src/cleanup/runMatching.ts</c> (<c>NOT_A_FULL_CHECK</c>) on origin/feat/wc-e6-cleanup-logs — until it
    /// reads <c>contracts/history-reasons.json</c> instead (E6.S4, plan §15o).</summary>
    private static readonly string[] FollowerPrefixes =
    [
        "refused: its request could not be used",
        "the run wrote its detail and ended before its history line",
        "the run left a detail that cannot be read",
    ];

    private RunId End(Ending ending) =>
        _endings[ending](_harness.Plant("collect", [RunKinds.FullCheckName], TimeSpan.FromSeconds(5), pid: 70));

    private static RunId Ran(RunRequestFile request, CliHost host, ExitCode expected)
    {
        CliRun.Over(host, "act", "--request", request.RunId.Text).Exit.Should().Be((int)expected);
        return request.RunId;
    }

    private RunId ObserveOnly(RunRequestFile request)
    {
        _harness.Sandbox.Write("/home/me/.config/wsl-care/config.json", "{ \"journal\": { \"keepDays\": 0 } }");
        return Ran(request, _harness.Host(), ExitCode.Ok);
    }

    private RunId RefusedAtTheLock(RunRequestFile request)
    {
        _harness.MachineLayer("""{ "requests": { "lockWaitSeconds": 0 } }""");
        var held = (Core.Files.ExclusiveLock.Held)RunLock.TryTake(_harness.Sandbox.Paths, _harness.Sandbox.Files);
        using (held.Handle)
        {
            return Ran(request, _harness.Host(), ExitCode.Busy);
        }
    }

    /// <summary>A stale request of another run is asked of systemd during the run's sweep; the signal arrives then.</summary>
    private RunId CutOffWhileSweeping(RunRequestFile request)
    {
        var stale = _harness.Plant("act", ["A9"], TimeSpan.FromMinutes(30), pid: 61);
        return CutOffBy(argv => argv.SequenceEqual(UnitCommands.Show(stale.RunId).Argv), request);
    }

    private RunId CutOffBeforeItStarted(RunRequestFile request)
    {
        Writer().Append(request.TerminalLine(Now, Now, RunOutcome.Interrupted, ActionStatus.Interrupted, DetachedRuns.CutOffReason("SIGTERM (test)")));
        return request.RunId;
    }

    private RunId SweptRequest(RunRequestFile request)
    {
        var old = _harness.Plant("collect", [RunKinds.FullCheckName], TimeSpan.FromMinutes(30), pid: 72);
        _harness.Runner.Script(UnitCommands.Show(old.RunId).Argv, 0, "ActiveState=inactive\nJob=\n");
        RequestSweep.ApplyAsync(_harness.Sandbox.Paths, _harness.Sandbox.Files, _harness.Runner, new FakeProcessTable(), Now, request.RunId).GetAwaiter().GetResult();
        return old.RunId;
    }

    private RunId SweptHolder(bool marked)
    {
        var dead = new RunningFile(1, RunId.New(Now.AddMinutes(-9), 4321), RunTrigger.Manual, [RunKinds.FullCheckName], RunKinds.FullCheckName, 4321, Now.AddMinutes(-10), Now.AddMinutes(-9), Now.AddMinutes(-5), RunKind.Collect);
        Directory.CreateDirectory(_harness.Sandbox.Paths.StateDirectory);
        File.WriteAllText(RunningState.File(_harness.Sandbox.Paths), JsonSerializer.Serialize(dead, WslCareJsonContext.Default.RunningFile));
        if (marked)
        {
            StopMarkers.Mark(_harness.Sandbox.Paths, _harness.Sandbox.Files, dead.RunId, "wsl-care-act@x.service", Now.AddMinutes(-4));
        }

        RunningSweep.Apply(_harness.Sandbox.Paths, _harness.Sandbox.Files, new FakeProcessTable(), Now, RunningReadRetry.Default, RunId.New(Now, 1), 1).Should().BeOfType<RunningSweep.Clear>();
        return dead.RunId;
    }

    /// <summary>The command <paramref name="when"/> matches is where the signal arrives.</summary>
    private RunId CutOffBy(Func<IReadOnlyList<string>, bool> when, RunRequestFile request)
    {
        using var cancel = new CancellationTokenSource();
        _harness.Runner.ScriptEffect(when, _ =>
        {
            cancel.Cancel();
            throw new OperationCanceledException(cancel.Token);
        });

        var run = () => CliRun.Over(_harness.Host(), Serilog.Core.Logger.None, cancel.Token, "act", "--request", request.RunId.Text);

        run.Should().Throw<OperationCanceledException>();
        return request.RunId;
    }

    private void StageOrphan(RunId runId, string json)
    {
        var path = RunDetailStore.Absolute(_harness.Sandbox.Paths, RunDetailStore.RelativePath(runId));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private RunRecordWriter Writer() => new(_harness.Sandbox.Paths, _harness.Sandbox.Files);

    /// <summary>The ONE history line of <paramref name="runId"/>, as raw JSON.</summary>
    private JsonElement LineOf(RunId runId)
    {
        var lines = File.ReadAllLines(RunRecordWriter.HistoryFileIn(_harness.Sandbox.Paths))
            .Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("runId").GetString() == runId.Text)
            .ToList();
        lines.Should().ContainSingle($"run {runId} has exactly one terminal line");
        return lines[0];
    }

    private static string Kind(JsonElement line) => line.TryGetProperty("kind", out var kind) ? kind.GetString() ?? string.Empty : string.Empty;

    private static string Reason(JsonElement line) => line.TryGetProperty("reason", out var reason) ? reason.GetString() ?? string.Empty : string.Empty;

    private static IReadOnlyList<string> ActionIds(JsonElement line) =>
        [.. line.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("id").GetString() ?? string.Empty)];
}
