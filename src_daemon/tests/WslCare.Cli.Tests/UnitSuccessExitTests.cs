using System.Globalization;
using System.Reflection;
using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Actions.Engine;
using WslCare.Core.Json;
using WslCare.Core.Records;
using WslCare.Core.Systemd;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// Retro round over PR #11 (O3): a <c>Type=oneshot</c> unit counts only 0 and its <c>SuccessExitStatus</c> as a clean end, so
/// every RECORDED end of the run it starts must exit 0 or a listed code — and nothing unrecorded may (the health collector counts
/// failed units). The test this replaces compared the unit's list with a list typed beside it — circular: an unusable request
/// recorded <c>refused</c> exited 2 and a run recorded <c>interrupted</c> after <c>act --stop</c> exited 130, both outside the
/// list, and it stayed green. This one DRIVES every ending of the two units' runs through the whole program as <c>Main</c> runs
/// it (<c>Program.Guarded</c>), OBSERVES whether the end was answered — a terminal history line names the run, or the run
/// changed nothing at all — and derives the list each unit must carry from the exits the code returned.
/// </summary>
/// <remarks>The endings are the branches of <c>DetachedRuns.FromRequest</c> and of <c>collect --timer</c>; a new branch is added
/// to <see cref="DetachedEnding"/> / <see cref="TimerEnding"/> in the same change (the enumeration is the deliverable). PR43 gate
/// round #3: so that adding an exit FORCES that decision, every <see cref="ExitCode"/> member is classified per unit in
/// <see cref="Classified"/> — a recorded answer, a failure, or not reachable from the unit — held complete against the enum, the
/// units' lists derived from it, and the driven endings held to agree with it.</remarks>
public sealed class UnitSuccessExitTests
{
    private static readonly DateTimeOffset Now = DetachedRunHarness.Now;

    /// <summary>Every way <c>act --request</c> (<c>wsl-care-act@.service</c>) ends.</summary>
    public enum DetachedEnding
    {
        Completed,
        ActionFailed,
        RefusedAtTheLock,
        RefusedWedged,
        RefusedStateUnreadable,
        RefusedObserveOnly,
        UnusableRequest,
        NoRequest,
        AlreadyRecorded,
        StoppedAndRecorded,
        LineNotWritten,
    }

    /// <summary>Every way <c>watch --timer</c> (<c>wsl-care-watch.service</c>, plan E14 S2b) ends.</summary>
    public enum WatchEnding
    {
        Sampled,
        Acted,
        Busy,
        StoppedWhileSampling,
        LineNotWritten,
    }

    /// <summary>Every way <c>collect --timer</c> (<c>wsl-care.service</c>) ends.</summary>
    public enum TimerEnding
    {
        Completed,
        Busy,
        StoppedAndRecorded,
        LineNotWritten,
    }

    /// <summary>What an exit code means for the run a unit starts.</summary>
    private enum UnitAnswer
    {
        /// <summary>A recorded end (or a no-op): the unit must count it as success.</summary>
        Answer,

        /// <summary>An end that was not recorded, or a defect: a failed unit.</summary>
        Failure,

        /// <summary>The unit's verb never returns it (another verb's code).</summary>
        NotReachable,
    }

    /// <summary>EVERY exit code, for the template unit's <c>act --request</c> and the timer's <c>collect --timer</c>. A member
    /// added to <see cref="ExitCode"/> without a row here fails <c>Every_exit_code_is_classified_for_both_units</c>.</summary>
    private static readonly IReadOnlyDictionary<ExitCode, (UnitAnswer Detached, UnitAnswer Timer, UnitAnswer Watch)> Classified = new Dictionary<ExitCode, (UnitAnswer, UnitAnswer, UnitAnswer)>
    {
        [ExitCode.Ok] = (UnitAnswer.Answer, UnitAnswer.Answer, UnitAnswer.Answer),
        [ExitCode.RunFailed] = (UnitAnswer.Failure, UnitAnswer.Failure, UnitAnswer.Failure),
        [ExitCode.Usage] = (UnitAnswer.Failure, UnitAnswer.Failure, UnitAnswer.Failure),
        [ExitCode.ActionFailed] = (UnitAnswer.Answer, UnitAnswer.NotReachable, UnitAnswer.NotReachable),
        [ExitCode.RecordsUnreadable] = (UnitAnswer.NotReachable, UnitAnswer.NotReachable, UnitAnswer.NotReachable),
        [ExitCode.DetachUnavailable] = (UnitAnswer.NotReachable, UnitAnswer.NotReachable, UnitAnswer.NotReachable),
        [ExitCode.Internal] = (UnitAnswer.Failure, UnitAnswer.Failure, UnitAnswer.Failure),
        [ExitCode.DetachStartFailed] = (UnitAnswer.NotReachable, UnitAnswer.NotReachable, UnitAnswer.NotReachable),
        [ExitCode.QueueFull] = (UnitAnswer.NotReachable, UnitAnswer.NotReachable, UnitAnswer.NotReachable),
        [ExitCode.Busy] = (UnitAnswer.Answer, UnitAnswer.Answer, UnitAnswer.Answer),
        [ExitCode.Wedged] = (UnitAnswer.Answer, UnitAnswer.NotReachable, UnitAnswer.NotReachable),
        [ExitCode.NeedsRoot] = (UnitAnswer.Failure, UnitAnswer.NotReachable, UnitAnswer.Failure),
        [ExitCode.ObserveOnly] = (UnitAnswer.Answer, UnitAnswer.NotReachable, UnitAnswer.NotReachable),
        [ExitCode.StateUnreadable] = (UnitAnswer.Answer, UnitAnswer.NotReachable, UnitAnswer.NotReachable),
        [ExitCode.RequestGone] = (UnitAnswer.Answer, UnitAnswer.NotReachable, UnitAnswer.NotReachable),
        [ExitCode.NotAsRoot] = (UnitAnswer.NotReachable, UnitAnswer.NotReachable, UnitAnswer.NotReachable),
        [ExitCode.RequestUnusable] = (UnitAnswer.Answer, UnitAnswer.NotReachable, UnitAnswer.NotReachable),
        // E14 S6: only the busy verb returns it, and neither unit runs that verb.
        [ExitCode.MachineBusy] = (UnitAnswer.NotReachable, UnitAnswer.NotReachable, UnitAnswer.NotReachable),
        [ExitCode.Interrupted] = (UnitAnswer.Answer, UnitAnswer.Answer, UnitAnswer.Answer),
    };

    private static UnitAnswer AnswerOf(string unit, int exit) =>
        Classified.TryGetValue((ExitCode)exit, out var answer) ? unit switch { DetachedUnit => answer.Detached, WatchUnit => answer.Watch, _ => answer.Timer } : UnitAnswer.NotReachable;

    private const string DetachedUnit = "wsl-care-act@.service";

    private const string TimerUnit = "wsl-care.service";

    /// <summary>Plan E14 S2b: the watch's run (<c>watch --timer</c>).</summary>
    private const string WatchUnit = "wsl-care-watch.service";

    /// <summary>One ending as observed: what the program exited with, and whether the end was answered (recorded or a no-op).</summary>
    private sealed record Observed(string Ending, int Exit, bool Answered);

    /// <summary>What a run can leave behind: the history's lines, running.json, the requests, the run details.</summary>
    private sealed record Trace(int Lines, string Running, string Requests, int Details)
    {
        public static Trace Of(DetachedRunHarness h)
        {
            var running = RunningState.File(h.Sandbox.Paths);
            var requests = RunRequests.Directory(h.Sandbox.Paths);
            return new(
                h.History().Count,
                File.Exists(running) ? File.ReadAllText(running) : string.Empty,
                Directory.Exists(requests) ? string.Join(",", Directory.EnumerateFiles(requests).Select(Path.GetFileName).Order(StringComparer.Ordinal)) : string.Empty,
                RunDetailStore.List(h.Sandbox.Paths, h.Sandbox.Files).Count);
        }
    }

    [Fact]
    public void Every_exit_code_is_classified_for_both_units()
    {
        Classified.Keys.Should().BeEquivalentTo(Enum.GetValues<ExitCode>(), "a new exit code is a decision for every unit: a recorded answer, a failure, or not reachable");
    }

    [Theory]
    [InlineData(DetachedUnit)]
    [InlineData(TimerUnit)]
    [InlineData(WatchUnit)]
    public void Each_unit_counts_exactly_its_classified_answers_as_success(string unit)
    {
        var answers = Classified.Keys.Where(code => code != ExitCode.Ok && AnswerOf(unit, (int)code) == UnitAnswer.Answer).Select(code => (int)code).Order().ToList();

        SuccessExits(unit).Should().Equal(answers, $"{unit}: SuccessExitStatus is derived from the classification of every exit code");
    }

    [Fact]
    public void The_detached_run_s_template_counts_exactly_the_exits_of_its_answered_endings_as_success()
    {
        var observed = Enum.GetValues<DetachedEnding>().Select(Detached).ToList();

        Hold(DetachedUnit, observed);
    }

    [Fact]
    public void The_timer_s_service_counts_exactly_the_exits_of_its_answered_endings_as_success()
    {
        var observed = Enum.GetValues<TimerEnding>().Select(Timer).ToList();

        Hold(TimerUnit, observed);
    }

    [Fact]
    public void The_watch_s_service_counts_exactly_the_exits_of_its_answered_endings_as_success()
    {
        var observed = Enum.GetValues<WatchEnding>().Select(Watch).ToList();

        Hold(WatchUnit, observed);
    }

    /// <summary>The unit's list is exactly the non-zero exits of the answered endings; every driven ending agrees with the
    /// classification (an answered end exits an Answer code, an unanswered one a Failure code) — a failure stays a failed unit.</summary>
    private static void Hold(string unit, IReadOnlyList<Observed> observed)
    {
        var listed = SuccessExits(unit);
        var answered = observed.Where(o => o.Answered && o.Exit != 0).Select(o => o.Exit).Distinct().Order().ToList();

        listed.Should().Equal(answered, $"{unit}: SuccessExitStatus is exactly what the recorded endings exit with — observed {Describe(observed)}");
        observed.Should().OnlyContain(o => AnswerOf(unit, o.Exit) == (o.Answered ? UnitAnswer.Answer : UnitAnswer.Failure), $"the driven endings agree with the classification — observed {Describe(observed)}");
        observed.Should().Contain(o => !o.Answered, "a file of answers needs one failure: the predicate can tell them apart");
    }

    private static string Describe(IEnumerable<Observed> observed) => string.Join("; ", observed.Select(o => $"{o.Ending} {o.Exit}{(o.Answered ? string.Empty : " (not recorded)")}"));

    private static Observed Detached(DetachedEnding ending)
    {
        using var h = new DetachedRunHarness($"unit-exit-{ending}");
        var runId = Stage(h, ending);
        var before = Trace.Of(h);
        var exit = RunDetached(h, ending, runId);
        var answered = h.History().Any(r => r.RunId == runId) || Trace.Of(h) == before;
        return new Observed(ending.ToString(), exit, answered);
    }

    /// <summary>The world of one ending, and the run id <c>act --request</c> is started with.</summary>
    private static RunId Stage(DetachedRunHarness h, DetachedEnding ending) => ending switch
    {
        DetachedEnding.NoRequest => RunId.New(Now, 41),
        DetachedEnding.UnusableRequest => PlantUnusable(h, RunId.New(Now, 42)),
        _ => StageRequest(h, ending),
    };

    private static RunId PlantUnusable(DetachedRunHarness h, RunId runId)
    {
        WriteState(RunRequests.File(h.Sandbox.Paths, runId), "{ not json");
        return runId;
    }

    /// <summary>A usable request, and what else the ending needs beside it (<see cref="Staging"/>).</summary>
    private static RunId StageRequest(DetachedRunHarness h, DetachedEnding ending)
    {
        var request = h.Plant("act", ["A10"], TimeSpan.FromSeconds(5));
        Staging(ending)(h, request.RunId);
        return request.RunId;
    }

    /// <summary>What one ending stages beside its request (PR43 gate round #5: a switch expression, not a branching method).</summary>
    private static Action<DetachedRunHarness, RunId> Staging(DetachedEnding ending) => ending switch
    {
        DetachedEnding.ActionFailed => static (h, _) => h.Runner.Script(["journalctl", "--vacuum-time=30d"], 1, stderr: "Failed to vacuum (test)"),
        DetachedEnding.RefusedWedged => static (h, _) => PlantRunning(h, RunId.New(Now.AddMinutes(-9), 999), heartbeat: Now.AddMinutes(-5)),
        DetachedEnding.RefusedStateUnreadable => static (h, _) => WriteState(RunningState.File(h.Sandbox.Paths), "{}"),
        DetachedEnding.RefusedObserveOnly => static (h, _) => h.Sandbox.Write("/home/me/.config/wsl-care/config.json", "{ \"journal\": { \"keepDays\": 0 } }"),
        DetachedEnding.AlreadyRecorded => static (h, id) => new RunRecordWriter(h.Sandbox.Paths, h.Sandbox.Files).Append(new RunRecord(1, id, RunTrigger.Manual, Now, Now, RunOutcome.Completed, [], RunKind.Act)),
        DetachedEnding.RefusedAtTheLock => static (h, _) => h.MachineLayer("""{ "requests": { "lockWaitSeconds": 0 } }"""),
        _ => NoStaging,
    };

    /// <summary>An ending that needs nothing beside its request.</summary>
    private static readonly Action<DetachedRunHarness, RunId> NoStaging = static (_, _) => { };

    private static int RunDetached(DetachedRunHarness h, DetachedEnding ending, RunId runId)
    {
        var host = ending switch
        {
            DetachedEnding.RefusedWedged => h.Host() with { Processes = new FakeProcessTable().Alive(999, Now.AddHours(-1)) },
            DetachedEnding.LineNotWritten => h.Host(files: new RefusingHistoryAppends(h.Sandbox.Files)),
            _ => h.Host(),
        };
        return ending switch
        {
            DetachedEnding.RefusedAtTheLock => UnderTheLock(h, () => CliRun.Guarded(host, CancellationToken.None, "act", "--request", runId.Text).Exit),
            DetachedEnding.StoppedAndRecorded => Stopped(h, ["journalctl", "--vacuum-time=30d"], token => CliRun.Guarded(host, token, "act", "--request", runId.Text).Exit),
            _ => CliRun.Guarded(host, CancellationToken.None, "act", "--request", runId.Text).Exit,
        };
    }

    private static Observed Timer(TimerEnding ending)
    {
        using var h = new DetachedRunHarness($"unit-exit-timer-{ending}");
        if (ending == TimerEnding.Busy)
        {
            // Plan E14 S2b: the timer's run waits requests.lockWaitSeconds for the lock; held throughout, it is refused at once here.
            h.MachineLayer("""{ "requests": { "lockWaitSeconds": 0 } }""");
        }

        var before = Trace.Of(h);
        var host = ending == TimerEnding.LineNotWritten ? h.Host(files: new RefusingHistoryAppends(h.Sandbox.Files)) : h.Host();
        var exit = ending switch
        {
            TimerEnding.Busy => UnderTheLock(h, () => CliRun.Guarded(host, CancellationToken.None, "collect", "--timer").Exit),
            TimerEnding.StoppedAndRecorded => Stopped(h, SystemdCommands.JournalDiskUsage.Argv, token => CliRun.Guarded(host, token, "collect", "--timer").Exit),
            _ => CliRun.Guarded(host, CancellationToken.None, "collect", "--timer").Exit,
        };
        var answered = h.History().Count > before.Lines || Trace.Of(h) == before;
        return new Observed(ending.ToString(), exit, answered);
    }

    /// <summary>One ending of <c>watch --timer</c>. Its A19 is a test double with ONE target, so an act happens without a process
    /// table: the watch's own gates (the timer mark, auto.A19, no dry run, a target not tried) are what the ending drives.</summary>
    private static Observed Watch(WatchEnding ending)
    {
        using var h = new DetachedRunHarness($"unit-exit-watch-{ending}");
        if (ending is WatchEnding.Acted or WatchEnding.LineNotWritten)
        {
            h.MachineLayer("""{ "dryRun": false }""");
            WriteState(Core.Actions.Engine.DryRunWindow.File(h.Sandbox.Paths), JsonSerializer.Serialize(new FirstTimerRun(1, Now.AddDays(-8)), WslCareJsonContext.Default.FirstTimerRun));
        }

        var before = Trace.Of(h);
        var host = WatchHost(h, ending);
        var exit = ending switch
        {
            WatchEnding.Busy => UnderTheLock(h, () => CliRun.Guarded(host, CancellationToken.None, "watch", "--timer").Exit),
            WatchEnding.StoppedWhileSampling => StoppedWhileSampling(h, host),
            _ => CliRun.Guarded(host, CancellationToken.None, "watch", "--timer").Exit,
        };
        var answered = h.History().Count > before.Lines || Trace.Of(h) == before;
        return new Observed(ending.ToString(), exit, answered);
    }

    private static CliHost WatchHost(DetachedRunHarness h, WatchEnding ending)
    {
        var host = h.Host(files: ending == WatchEnding.LineNotWritten ? new RefusingHistoryAppends(h.Sandbox.Files) : null);
        return host with { Actions = new Core.Actions.ActionRegistry([new OneTargetA19()]) };
    }

    /// <summary>A stop (SIGTERM, as systemd asks on <c>systemctl stop</c>) while the watch samples — the probe is where it is cut.</summary>
    private static int StoppedWhileSampling(DetachedRunHarness h, CliHost host)
    {
        using var stop = new CancellationTokenSource();
        return CliRun.Guarded(host with { Probe = new StoppingProbe(stop) }, stop.Token, "watch", "--timer").Exit;
    }

    /// <summary>A probe that asks the run to stop and is cut off by that stop.</summary>
    private sealed class StoppingProbe(CancellationTokenSource stop) : Core.Hosting.IHostProbe
    {
        public Core.Hosting.HostSide Side => Core.Hosting.HostSide.Wsl;

        public Core.Collectors.ProbeSample Sample(CancellationToken cancellationToken)
        {
            stop.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new System.Diagnostics.UnreachableException("the token was cancelled");
        }
    }

    /// <summary>A19 as a test double: one target (pid:start of nobody real), run "ended" without signalling anything.</summary>
    private sealed class OneTargetA19 : Core.Actions.ICleanupAction, Core.Actions.IBoundToShownList
    {
        public Core.Actions.ActionId Id { get; } = Core.Actions.ActionId.Find("A19")!;

        public string Summary => "a test double of A19";

        public Core.Processes.Policy.CommandScope Scope => Core.Processes.Policy.CommandScope.User;

        public Core.Actions.IdleRule Idle => Core.Actions.IdleRule.Never;

        public IReadOnlyList<Core.Hosting.HostSide> Sides { get; } = [Core.Hosting.HostSide.Wsl];

        public IReadOnlyList<Core.Processes.Policy.CommandTemplate> Commands { get; } = [];

        private static readonly Core.Actions.ActionItem Target = new("process", "4242 coai-mcp", 1) { Key = "4242:4000:500:1000" };

        public IReadOnlyList<string> Shown(Core.Actions.ActionPreview preview) => ["4242:4000"];

        public Task<Core.Actions.ActionPreview> PreviewAsync(Core.Actions.ActionContext context, Core.Actions.ActionCommands commands, CancellationToken cancellationToken) =>
            Task.FromResult(Core.Actions.ActionPreview.Of("a test double", 1, null, "scripted", new Dictionary<string, long>(), string.Empty, [Target]));

        public Core.Actions.TriggerDecision Trigger(Core.Actions.ActionPreview preview, Core.Config.EffectiveConfig config) => new(preview.Count > 0, "any");

        public Task<Core.Actions.ActionRun> RunAsync(Core.Actions.ActionContext context, Core.Actions.ActionPreview preview, Core.Actions.ActionCommands commands, CancellationToken cancellationToken) =>
            Task.FromResult(new Core.Actions.ActionRun(1, null, "a test double", null, null, [Target], commands.Ran, string.Empty));
    }

    private static int UnderTheLock(DetachedRunHarness h, Func<int> run)
    {
        var held = (Core.Files.ExclusiveLock.Held)RunLock.TryTake(h.Sandbox.Paths, h.Sandbox.Files);
        using (held.Handle)
        {
            return run();
        }
    }

    /// <summary>A stop (SIGTERM, as <c>act --stop</c> asks systemd for) arriving while <paramref name="during"/> runs.</summary>
    private static int Stopped(DetachedRunHarness h, IReadOnlyList<string> during, Func<CancellationToken, int> run)
    {
        using var stop = new CancellationTokenSource();
        h.Runner.ScriptEffect(argv => argv.SequenceEqual(during), _ =>
        {
            stop.Cancel();
            throw new OperationCanceledException(stop.Token);
        });
        return run(stop.Token);
    }

    private static void PlantRunning(DetachedRunHarness h, RunId runId, DateTimeOffset heartbeat)
    {
        var file = new RunningFile(1, runId, RunTrigger.Timer, ["A10"], "A10", 999, Now.AddHours(-1), Now.AddMinutes(-9), heartbeat, RunKind.Act);
        WriteState(RunningState.File(h.Sandbox.Paths), JsonSerializer.Serialize(file, WslCareJsonContext.Default.RunningFile));
    }

    private static void WriteState(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>The unit's <c>SuccessExitStatus</c>, as numbers in order.</summary>
    private static IReadOnlyList<int> SuccessExits(string unit)
    {
        var directory = typeof(UnitSuccessExitTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "WslCare.SystemdDirectory").Value!;
        var line = File.ReadAllLines(Path.Combine(directory, unit)).Select(l => l.Trim()).Should().ContainSingle(l => l.StartsWith("SuccessExitStatus=", StringComparison.Ordinal)).Subject;
        return [.. line["SuccessExitStatus=".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(c => int.Parse(c, CultureInfo.InvariantCulture)).Order()];
    }
}
