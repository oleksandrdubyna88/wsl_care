using System.Text;
using System.Text.Json;

using FluentAssertions;

using WslCare.Cli.Commands;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Processes.Policy;
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
    private static readonly ProcessPrivilege Root = new(true, "a test says so");
    private static readonly DateTimeOffset Now = FixedTimeProvider.DefaultNow;

    private readonly LinuxSandbox _sandbox = new("full-check-line");
    private readonly RecordingCommandRunner _runner = new RecordingCommandRunner { Policy = CommandPolicy.Product }
        .Script(SystemdCommands.JournalDiskUsage.Argv, 0, "Archived and active journals take up 1.5G in the file system.");

    public FullCheckLineTests()
    {
        _sandbox.Write("/etc/passwd", "root:x:0:0::/root:/bin/bash\nme:x:1000:1000::/home/me:/bin/bash\n");
        Directory.CreateDirectory(_sandbox.Paths.DistroPath("/run/systemd/system"));
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

        /// <summary><c>DetachedRuns.CutOff</c>.</summary>
        CutOffBeforeItStarted,

        /// <summary><c>RequestSweep.Interrupted</c>: its unit is gone.</summary>
        SweptRequest,

        /// <summary><c>RunningSweep.SweepDead</c>: a dead holder of <c>running.json</c>.</summary>
        SweptHolder,

        /// <summary><c>RunningSweep.SweepDead</c> after <c>act --stop</c> marked it.</summary>
        SweptHolderAfterAStop,
    }

    public static TheoryData<Ending> Endings => [.. Enum.GetValues<Ending>()];

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
        [Ending.SweptHolderAfterAStop] = ("interrupted", "stopped: act --stop asked systemd"),
    };

    public void Dispose() => _sandbox.Dispose();

    [Theory]
    [MemberData(nameof(Endings))]
    public void Every_terminal_line_of_a_detached_full_check_names_it_by_kind_and_carries_no_collect_row(Ending ending)
    {
        var line = LineOf(End(ending));

        line.GetProperty("outcome").GetString().Should().Be(Expected[ending].Outcome, line.GetRawText());
        var reason = line.TryGetProperty("reason", out var r) ? r.GetString() ?? string.Empty : string.Empty;
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
        var line = LineOf(End(ending));
        var reason = line.TryGetProperty("reason", out var r) ? r.GetString() ?? string.Empty : string.Empty;

        HistoryReasons.MarksNotAFullCheck(reason).Should().BeFalse($"the reason \"{reason}\" of a full check {ending} must start with none of {string.Join(" | ", HistoryReasons.NotAFullCheckWithoutKind.Select(p => p.Prefix))}");
    }

    /// <summary>The companion: the prefixes DO mark the kind-less lines they exist for — a list that matched nothing would pass
    /// the test above forever. A reconciled orphan whose detail reads carries its kind, and its reason still starts with the
    /// reconcile's prefix: the contract applies to lines WITHOUT a kind, a line with one is told by its kind.</summary>
    [Fact]
    public void The_prefixes_mark_the_unusable_request_and_the_reconciled_orphan_lines_they_exist_for()
    {
        RequestSweep.Unusable(_sandbox.Paths, _sandbox.Files, Now, RunId.New(Now, 91), "not JSON (test)");
        StageOrphan(RunId.New(Now, 92), "{}");
        StageOrphan(RunId.New(Now, 93), JsonSerializer.Serialize(new RunDetailHead(1, RunId.New(Now, 93), RunTrigger.Manual, Now, Now, false), WslCareJsonContext.Default.RunDetailHead));
        RunReconcile.Apply(_sandbox.Paths, _sandbox.Files, Now);

        var unusable = LineOf(RunId.New(Now, 91));
        var unreadable = LineOf(RunId.New(Now, 92));
        var orphan = LineOf(RunId.New(Now, 93));
        foreach (var kindless in new[] { unusable, unreadable })
        {
            Kind(kindless).Should().BeEmpty("its kind cannot be known");
            HistoryReasons.MarksNotAFullCheck(kindless.GetProperty("reason").GetString()!).Should().BeTrue(kindless.GetRawText());
        }

        Kind(orphan).Should().Be("collect", "a full run's detail carries no kind of its own");
        HistoryReasons.MarksNotAFullCheck(orphan.GetProperty("reason").GetString()!).Should().BeTrue("kind first: the prefix is only the fallback");
    }

    private RunId End(Ending ending)
    {
        var request = Plant("collect", [RunKinds.FullCheckName], TimeSpan.FromSeconds(5), pid: 70);
        switch (ending)
        {
            case Ending.Completed:
                CliRun.Over(Host(), "act", "--request", request.RunId.Text).Exit.Should().Be((int)ExitCode.Ok);
                break;
            case Ending.ObserveOnly:
                _sandbox.Write("/home/me/.config/wsl-care/config.json", "{ \"journal\": { \"keepDays\": 0 } }");
                CliRun.Over(Host(), "act", "--request", request.RunId.Text).Exit.Should().Be((int)ExitCode.Ok);
                break;
            case Ending.FailedOnItsDetail:
                CliRun.Over(Host(new RefusingDetailWrites(_sandbox.Files)), "act", "--request", request.RunId.Text).Exit.Should().Be((int)ExitCode.RunFailed);
                break;
            case Ending.RefusedAtTheLock:
                RefusedAtTheLock(request);
                break;
            default:
                return EndWithoutFinishing(ending, request);
        }

        return request.RunId;
    }

    private RunId EndWithoutFinishing(Ending ending, RunRequestFile request)
    {
        switch (ending)
        {
            case Ending.CutOffDuringTheMeasurement:
                CutOffBy(argv => argv.SequenceEqual(SystemdCommands.JournalDiskUsage.Argv), request);
                break;
            case Ending.CutOffWhileSweeping:
                var stale = Plant("act", ["A9"], TimeSpan.FromMinutes(30), pid: 61);
                CutOffBy(argv => argv.SequenceEqual(UnitCommands.Show(stale.RunId).Argv), request);
                break;
            case Ending.CutOffBeforeItStarted:
                // Not reachable from outside for a full check (CollectRun records its own cut-offs first); the line is built by
                // the very expression DetachedRuns.CutOff appends.
                Writer().Append(request.TerminalLine(Now, Now, RunOutcome.Interrupted, ActionStatus.Interrupted, DetachedRuns.CutOffReason("SIGTERM (test)")));
                break;
            default:
                return Swept(ending, request);
        }

        return request.RunId;
    }

    private RunId Swept(Ending ending, RunRequestFile request)
    {
        if (ending == Ending.SweptRequest)
        {
            var old = Plant("collect", [RunKinds.FullCheckName], TimeSpan.FromMinutes(30), pid: 72);
            _runner.Script(UnitCommands.Show(old.RunId).Argv, 0, "ActiveState=inactive\nJob=\n");
            RequestSweep.ApplyAsync(_sandbox.Paths, _sandbox.Files, _runner, new FakeProcessTable(), Now, request.RunId).GetAwaiter().GetResult();
            return old.RunId;
        }

        var dead = new RunningFile(1, RunId.New(Now.AddMinutes(-9), 4321), RunTrigger.Manual, [RunKinds.FullCheckName], RunKinds.FullCheckName, 4321, Now.AddMinutes(-10), Now.AddMinutes(-9), Now.AddMinutes(-5), RunKind.Collect);
        Directory.CreateDirectory(_sandbox.Paths.StateDirectory);
        File.WriteAllText(RunningState.File(_sandbox.Paths), JsonSerializer.Serialize(dead, WslCareJsonContext.Default.RunningFile));
        if (ending == Ending.SweptHolderAfterAStop)
        {
            StopMarkers.Mark(_sandbox.Paths, _sandbox.Files, dead.RunId, "wsl-care-act@x.service", Now.AddMinutes(-4));
        }

        RunningSweep.Apply(_sandbox.Paths, _sandbox.Files, new FakeProcessTable(), Now, RunningReadRetry.Default, request.RunId, 1).Should().BeOfType<RunningSweep.Clear>();
        return dead.RunId;
    }

    private void RefusedAtTheLock(RunRequestFile request)
    {
        var held = (ExclusiveLock.Held)RunLock.TryTake(_sandbox.Paths, _sandbox.Files);
        using (held.Handle)
        {
            CliRun.Over(Host(), "act", "--request", request.RunId.Text).Exit.Should().Be((int)ExitCode.Busy);
        }
    }

    /// <summary>The command <paramref name="when"/> matches is where the signal arrives.</summary>
    private void CutOffBy(Func<IReadOnlyList<string>, bool> when, RunRequestFile request)
    {
        using var cancel = new CancellationTokenSource();
        _runner.ScriptEffect(when, _ =>
        {
            cancel.Cancel();
            throw new OperationCanceledException(cancel.Token);
        });

        var run = () => CliRun.Over(Host(), Serilog.Core.Logger.None, cancel.Token, "act", "--request", request.RunId.Text);

        run.Should().Throw<OperationCanceledException>();
    }

    private void StageOrphan(RunId runId, string json)
    {
        var path = RunDetailStore.Absolute(_sandbox.Paths, RunDetailStore.RelativePath(runId));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private CliHost Host(IFileSystem? files = null) =>
        new(_sandbox.Paths, files ?? _sandbox.Files, new FixedTimeProvider(), _runner)
        {
            Privilege = Root,
            Processes = new FakeProcessTable(),
            Probe = new FakeProbe(HostSide.Wsl, new FixedTimeProvider()),
            StandardInput = () => new MemoryStream(Encoding.UTF8.GetBytes(string.Empty)),
        };

    private RunRecordWriter Writer() => new(_sandbox.Paths, _sandbox.Files);

    private RunRequestFile Plant(string kind, IReadOnlyList<string> actions, TimeSpan age, int pid)
    {
        var request = new RunRequestFile(1, RunId.New(Now - age, pid), kind, actions, RunTrigger.Manual, Now - age);
        RunRequests.Create(_sandbox.Paths, _sandbox.Files, request).Should().BeOfType<ExclusiveCreate.Created>();
        return request;
    }

    /// <summary>The ONE history line of <paramref name="runId"/>, as raw JSON.</summary>
    private JsonElement LineOf(RunId runId)
    {
        var lines = File.ReadAllLines(RunRecordWriter.HistoryFileIn(_sandbox.Paths))
            .Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("runId").GetString() == runId.Text)
            .ToList();
        lines.Should().ContainSingle($"run {runId} has exactly one terminal line");
        return lines[0];
    }

    private static string Kind(JsonElement line) => line.TryGetProperty("kind", out var kind) ? kind.GetString() ?? string.Empty : string.Empty;

    private static IReadOnlyList<string> ActionIds(JsonElement line) =>
        [.. line.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("id").GetString() ?? string.Empty)];
}
