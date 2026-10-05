using System.Text.Json;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Docker;
using WslCare.Core.Files;
using WslCare.Core.Json;
using WslCare.Core.Records;
using WslCare.Core.Systemd;
using WslCare.FakeTool;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// The worlds E6.S0's read contract is observed in (plan §15j) — staged ONCE here and used by both
/// <see cref="ReadContractFlows"/> (which asserts what each answer means) and <see cref="GoldenContracts"/> (which writes the
/// answers down for the extension). Every instant that is not judged against the clock is FIXED (<see cref="Staged"/>), so
/// the goldens need as few normalisation rules as possible.
/// </summary>
internal static class ReadContractScenes
{
    /// <summary>The fixed instant a staged run started at (a dead run is judged by its pid, never by time).</summary>
    public static readonly DateTimeOffset Staged = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    /// <summary>A pid no process has: the largest a pid can be spelt as, far above any kernel's <c>pid_max</c>.</summary>
    public const int GonePid = int.MaxValue;

    /// <summary>A run id nothing names — the one the goldens normalise every run id to.</summary>
    public const string StrangerRunId = "20000101T000000Z-1";

    /// <summary>The running states a scenario can stage against the real process table (<c>unknown</c> needs a pid the OS
    /// refuses to inspect, which an unprivileged test cannot make on cue).</summary>
    public static IReadOnlyList<string> StagedRunningStates { get; } = ["live", "wedged", "dead", "unreadable", "queued", "earlier-boot"];

    /// <summary>The local day 2026-10-02 at UTC+03:00 — it starts at 21:00Z the day before, so it crosses UTC midnight.</summary>
    public static IReadOnlyList<string> LocalDay { get; } = ["--from", "2026-10-02T00:00:00+03:00", "--to", "2026-10-03T00:00:00+03:00"];

    /// <summary>A4's synthetic morning: 387 anonymous volumes, as on 2026-10-02.</summary>
    public const int A4Volumes = 387;

    /// <summary>Stages <paramref name="state"/> in the sandbox: a <c>running.json</c> or a request file, as a run would leave it.</summary>
    public static void Stage(ScenarioHome home, string state)
    {
        switch (state)
        {
            case "live":
                Running(home, Own(home, heartbeat: DateTimeOffset.UtcNow));
                break;
            case "wedged":
                Running(home, Own(home, heartbeat: DateTimeOffset.UtcNow.AddMinutes(-10)));
                break;
            case "dead":
                Running(home, Dead());
                break;
            case "unreadable":
                Write(RunningState.File(home.Paths), "{}"u8.ToArray());
                break;
            case "queued":
                var request = new RunRequestFile(1, RunId.New(Staged, 77), "act", ["A4"], RunTrigger.Manual, Staged) { Shown = SyntheticDocker.Names(3) };
                Write(RunRequests.File(home.Paths, request.RunId), JsonSerializer.SerializeToUtf8Bytes(request, WslCareJsonContext.Default.RunRequestFile));
                break;
            case "earlier-boot":
                // coai E6 code round #6: a request another boot wrote — reported dead by the real boot id the CLI reads.
                var orphan = new RunRequestFile(1, RunId.New(Staged, 78), "act", ["A10"], RunTrigger.Manual, Staged) { BootId = "an-earlier-boot", CreatedMonotonicMs = 1_000 };
                Write(RunRequests.File(home.Paths, orphan.RunId), JsonSerializer.SerializeToUtf8Bytes(orphan, WslCareJsonContext.Default.RunRequestFile));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "not a running state a scenario stages");
        }
    }

    /// <summary>A dead run's <c>running.json</c>: a pid no process has, fixed instants.</summary>
    public static RunningFile Dead() =>
        new(1, RunId.New(Staged, GonePid), RunTrigger.Manual, ["A5", "A4"], "A4", GonePid, Staged.AddSeconds(-1), Staged, Staged.AddMinutes(1), RunKind.Act);

    /// <summary>A run held by THIS test process — alive for the whole scenario — with its real start, so the CLI judges it by
    /// the real process table exactly as it would a running <c>act</c>.</summary>
    private static RunningFile Own(ScenarioHome home, DateTimeOffset heartbeat)
    {
        var pid = Environment.ProcessId;
        var start = new SystemProcessTable().Lookup(pid) is ProcessLookup.Alive alive ? alive.StartUtc : throw new InvalidOperationException("this test process cannot read its own start");
        return new RunningFile(1, RunId.New(Staged, pid), RunTrigger.Manual, ["A5", "A4"], "A4", pid, start, Staged, heartbeat, RunKind.Act);
    }

    public static void Running(ScenarioHome home, RunningFile file) => RunningState.Write(home.Paths, new PhysicalFileSystem(home.Paths), file);

    /// <summary>A sandbox with root CLAIMED and a fake <c>journalctl</c> that answers A10 — the captured disk usage, and a vacuum
    /// that frees nothing (after <paramref name="vacuumDelayMilliseconds"/>).</summary>
    public static ScenarioHome Journal(string purpose, int vacuumDelayMilliseconds = 0)
    {
        var home = new ScenarioHome(purpose) { ClaimsRoot = true };
        home.Script(SystemdCommands.Journalctl, SystemdCommands.JournalDiskUsage.Arguments, 0, $"docker/{DockerFixture.Name}/journalctl-disk-usage.out");
        home.Script(SystemdCommands.Journalctl, ["--vacuum-time=30d"], 0, stderr: "Vacuuming done, freed 0B of archived journals from /var/log/journal.", delayMilliseconds: vacuumDelayMilliseconds);
        return home;
    }

    /// <summary>A sandbox with root claimed, every age limit at 0 and <see cref="A4Volumes"/> synthetic anonymous volumes.</summary>
    public static (ScenarioHome Home, IReadOnlyList<string> Names) A4Morning(string purpose)
    {
        var home = new ScenarioHome(purpose) { ClaimsRoot = true };
        var names = SyntheticDocker.AnonymousVolumes(home, A4Volumes);
        Write(home.Paths.UserConfigFile, System.Text.Encoding.UTF8.GetBytes(PreviewFlows.AllAges));
        return (home, names);
    }

    /// <summary>A seeded history around the local day of <see cref="LocalDay"/>: one run just before it, the two at its edges
    /// (the first instant in, the end instant out), one on each side of UTC midnight inside it — with a button's A4 detail,
    /// a timer's dry run and metrics on the full runs.</summary>
    public static void LocalDayHistory(ScenarioHome home)
    {
        var files = new PhysicalFileSystem(home.Paths);
        var writer = new RunRecordWriter(home.Paths, files);
        void Line(DateTimeOffset at, RunTrigger trigger, IReadOnlyList<ActionRecord> actions, RunMetrics? metrics = null, string? detail = null, bool dry = false) =>
            writer.Append(new RunRecord(1, RunId.New(at, 5), trigger, at, at.AddMinutes(1), RunOutcome.Completed, actions, trigger == RunTrigger.Timer ? RunKind.Collect : RunKind.Act) { DryRun = dry, Metrics = metrics, Detail = detail });

        var before = new DateTimeOffset(2026, 10, 1, 20, 59, 59, TimeSpan.Zero);
        var first = new DateTimeOffset(2026, 10, 1, 21, 0, 0, TimeSpan.Zero);
        var lateUtc = new DateTimeOffset(2026, 10, 1, 23, 59, 59, TimeSpan.Zero);
        var midnight = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 10, 2, 21, 0, 0, TimeSpan.Zero);

        Line(before, RunTrigger.Timer, [new ActionRecord("A10", 1, 1_000) { Status = ActionStatus.Ran }], new RunMetrics(30.0, 14_000_000_000, 9_000_000_000, 500_000_000, 12.0, 20_000_000_000, 900));
        Line(first, RunTrigger.Timer, [new ActionRecord("A10", 2, 0) { Status = ActionStatus.DryRun, WouldFreeBytes = 2_000 }], new RunMetrics(28.5, 13_400_000_000, 9_500_000_000, 600_000_000, 12.1, 21_000_000_000, 950), dry: true);
        var button = RunId.New(lateUtc, 5);
        var outcome = new ActionOutcome("A4", "A4 summary", ActionStatus.Ran, "ran", null, new ActionRun(
            2, 308_000_000, "the docker system df -v sizes of exactly the volumes docker volume rm confirmed", 600_000_000, 292_000_000,
            [new ActionItem("volume", SyntheticDocker.Names(2)[0], 154_000_000), new ActionItem("volume", SyntheticDocker.Names(2)[1], 154_000_000)],
            [new ActionCommandRecord("docker volume rm <names>", $"docker volume rm {string.Join(' ', SyntheticDocker.Names(2))}", "exited", 0, string.Empty)],
            string.Empty));
        var detail = new ActRunDetail(1, button, RunTrigger.Manual, lateUtc, lateUtc.AddMinutes(1), false, "act", RunOutcome.Completed, "wsl", "a button never dry-runs", new TargetUserReport(false, null, null, "machine-scoped"), [outcome], []);
        RunDetailStore.Write(home.Paths, files, button, JsonSerializer.SerializeToUtf8Bytes(detail, WslCareJsonContext.Default.ActRunDetail));
        Line(lateUtc, RunTrigger.Manual, [ActionRecords.Of(outcome)], detail: RunDetailStore.RelativePath(button));
        Line(midnight, RunTrigger.Timer, [new ActionRecord("A10", 3, 3_000) { Status = ActionStatus.Ran }], new RunMetrics(41.0, 19_300_000_000, 7_000_000_000, 400_000_000, 12.2, 19_000_000_000, 1_000));
        Line(end, RunTrigger.Timer, [new ActionRecord("A10", 9, 9_000) { Status = ActionStatus.Ran }], new RunMetrics(35.0, 16_500_000_000, 8_000_000_000, 450_000_000, 12.3, 18_000_000_000, 1_100));
    }

    private static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }
}
