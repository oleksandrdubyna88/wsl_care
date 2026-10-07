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
/// to <see cref="DetachedEnding"/> / <see cref="TimerEnding"/> in the same change (the enumeration is the deliverable).</remarks>
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

    /// <summary>Every way <c>collect --timer</c> (<c>wsl-care.service</c>) ends.</summary>
    public enum TimerEnding
    {
        Completed,
        Busy,
        StoppedAndRecorded,
        LineNotWritten,
    }

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
    public void The_detached_run_s_template_counts_exactly_the_exits_of_its_answered_endings_as_success()
    {
        var observed = Enum.GetValues<DetachedEnding>().Select(Detached).ToList();

        Hold("wsl-care-act@.service", observed);
    }

    [Fact]
    public void The_timer_s_service_counts_exactly_the_exits_of_its_answered_endings_as_success()
    {
        var observed = Enum.GetValues<TimerEnding>().Select(Timer).ToList();

        Hold("wsl-care.service", observed);
    }

    /// <summary>The unit's list is exactly the non-zero exits of the answered endings; no unanswered ending exits 0 or a listed
    /// code (a failure must stay a failed unit).</summary>
    private static void Hold(string unit, IReadOnlyList<Observed> observed)
    {
        var listed = SuccessExits(unit);
        var answered = observed.Where(o => o.Answered && o.Exit != 0).Select(o => o.Exit).Distinct().Order().ToList();

        listed.Should().Equal(answered, $"{unit}: SuccessExitStatus is exactly what the recorded endings exit with — observed {Describe(observed)}");
        observed.Where(o => !o.Answered).Should().OnlyContain(o => o.Exit != 0 && !listed.Contains(o.Exit), $"an end that was not recorded is a failed unit — observed {Describe(observed)}");
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
    private static RunId Stage(DetachedRunHarness h, DetachedEnding ending)
    {
        switch (ending)
        {
            case DetachedEnding.NoRequest:
                return RunId.New(Now, 41);
            case DetachedEnding.UnusableRequest:
                var bad = RunId.New(Now, 42);
                WriteState(RunRequests.File(h.Sandbox.Paths, bad), "{ not json");
                return bad;
            default:
                return StageRequest(h, ending);
        }
    }

    private static RunId StageRequest(DetachedRunHarness h, DetachedEnding ending)
    {
        var request = h.Plant("act", ["A10"], TimeSpan.FromSeconds(5));
        switch (ending)
        {
            case DetachedEnding.ActionFailed:
                h.Runner.Script(["journalctl", "--vacuum-time=30d"], 1, stderr: "Failed to vacuum (test)");
                break;
            case DetachedEnding.RefusedWedged:
                PlantRunning(h, RunId.New(Now.AddMinutes(-9), 999), heartbeat: Now.AddMinutes(-5));
                break;
            case DetachedEnding.RefusedStateUnreadable:
                WriteState(RunningState.File(h.Sandbox.Paths), "{}");
                break;
            case DetachedEnding.RefusedObserveOnly:
                h.Sandbox.Write("/home/me/.config/wsl-care/config.json", "{ \"journal\": { \"keepDays\": 0 } }");
                break;
            case DetachedEnding.AlreadyRecorded:
                new RunRecordWriter(h.Sandbox.Paths, h.Sandbox.Files).Append(new RunRecord(1, request.RunId, RunTrigger.Manual, Now, Now, RunOutcome.Completed, [], RunKind.Act));
                break;
            case DetachedEnding.RefusedAtTheLock:
                h.MachineLayer("""{ "requests": { "lockWaitSeconds": 0 } }""");
                break;
        }

        return request.RunId;
    }

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
