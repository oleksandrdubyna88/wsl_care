using System.Globalization;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Cli.Commands;
using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
using WslCare.Core.Systemd;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// E6.S1 in-process (plan §15j B2, M2, M4, M9; §15k #1 / #2 / #8 / #14 / #17 / #18): <c>act … --confirm --detach</c> and
/// <c>collect --detach</c> (refusals 69 / 73 / 75 / 76 / 79, the request written, the unit started, a failed start removing the
/// request with 71, the <c>accepted</c> answer), <c>--only -</c> (the list on stdin under a byte cap and a time ceiling),
/// <c>act --request</c> (80 for no request, a refused run RECORDED, the run recorded under its request's id, the request gone)
/// and <c>act --stop</c> (only a wedged run, only through systemd, only when its process lives in one of the two units).
/// </summary>
public sealed class DetachedRunsTests : IDisposable
{
    private static readonly ProcessPrivilege Root = DetachedRunHarness.Root;
    private static readonly ProcessPrivilege NotRoot = DetachedRunHarness.NotRoot;
    private static readonly DateTimeOffset Now = DetachedRunHarness.Now;

    private readonly DetachedRunHarness _harness = new("detach-cli");

    public void Dispose() => _harness.Dispose();

    private LinuxSandbox Sandbox => _harness.Sandbox;

    private RecordingCommandRunner Runner => _harness.Runner;

    private CliHost Host(ProcessPrivilege? privilege = null, string stdin = "") => _harness.Host(privilege, stdin);

    private static string Names(int count) => string.Concat(Enumerable.Range(0, count).Select(i => i.ToString("x64", CultureInfo.InvariantCulture) + "\n"));

    private IReadOnlyList<RunRequestRead> Requests() => _harness.Requests();

    private IReadOnlyList<RunRecord> History() => _harness.History();

    private IEnumerable<string> Starts() => Runner.Commands.Where(c => c.StartsWith("systemctl start", StringComparison.Ordinal));

    private static HandOffReport HandOff(string stdout) => JsonSerializer.Deserialize(stdout, WslCareJsonContext.Default.HandOffReport)!;

    private RunRequestFile Plant(string kind, IReadOnlyList<string> actions, TimeSpan age, int pid = 77) => _harness.Plant(kind, actions, age, pid);

    // ---------- the parse ----------

    [Theory]
    [InlineData("act", "A10", "--preview", "--detach")]
    [InlineData("act", "A10", "--confirm", "--timer", "--detach")]
    [InlineData("act", "A10", "--confirm", "--detach", "--detach")]
    [InlineData("act", "--request")]
    [InlineData("act", "--request", "not-a-run-id")]
    [InlineData("act", "--request", "20261004T120000Z-1", "--json")]
    [InlineData("act", "--stop")]
    [InlineData("act", "--stop", "20261004T120000Z-01")]
    [InlineData("act", "--stop", "20261004T120000Z-1", "--confirm")]
    [InlineData("collect", "--timer", "--detach")]
    [InlineData("collect", "--detach", "--detach")]
    public void A_detach_a_request_or_a_stop_outside_its_one_shape_is_a_usage_refusal(params string[] argv)
    {
        CommandLine.Parse(argv).Should().BeOfType<Request.Failed>();
    }

    /// <summary>coai E6 code round #2: a bad run id names the value and the shape, one helper for every verb that takes one.</summary>
    [Theory]
    [InlineData("act", "--request")]
    [InlineData("act", "--stop")]
    [InlineData("runs", "show")]
    public void A_bad_run_id_is_refused_naming_the_value_and_the_shape_the_same_way_for_every_verb(string verb, string sub)
    {
        var failed = CommandLine.Parse([verb, sub, "not-a-run-id"]).Should().BeOfType<Request.Failed>().Subject;

        failed.Message.Should().Be($"\"wsl-care {verb} {sub}\": \"not-a-run-id\" is not a run id (yyyyMMddTHHmmssZ-<pid>, as runs and logs print it).");
    }

    [Fact]
    public void The_detach_shapes_parse_into_their_requests()
    {
        var act = CommandLine.Parse(["act", "A4", "--confirm", "--manual", "--detach", "--only", "-", "--json"]).Should().BeOfType<Request.Act>().Subject;
        act.Detach.Should().BeTrue();
        act.ShownOnStdin.Should().BeTrue();
        act.Manual.Should().BeTrue();
        CommandLine.Parse(["act", "--request", "20261004T120000Z-1"]).Should().Be(new Request.ActFromRequest(RunId.TryParse("20261004T120000Z-1")!));
        CommandLine.Parse(["act", "--stop", "20261004T120000Z-1", "--json"]).Should().Be(new Request.ActStop(RunId.TryParse("20261004T120000Z-1")!, Json: true));
        CommandLine.Parse(["collect", "--detach", "--json"]).Should().BeOfType<Request.Collect>().Which.Detach.Should().BeTrue();
    }

    // ---------- --detach ----------

    [Fact]
    public void A_confirmed_detach_writes_the_request_starts_its_unit_and_answers_accepted_at_once()
    {
        var (exit, stdout, stderr) = CliRun.Over(Host(), "act", "A10", "--confirm", "--manual", "--detach", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var answer = HandOff(stdout);
        answer.Result.Should().Be("accepted");
        answer.Kind.Should().Be("act");
        answer.Unit.Should().Be($"wsl-care-act@{answer.RunId}.service");
        answer.ProductVersion.Should().Be(Program.VersionText);
        var request = Requests().Should().ContainSingle().Which.Should().BeOfType<RunRequestRead.Parsed>().Subject.File;
        request.RunId.Text.Should().Be(answer.RunId, "the run records itself under the id the panel was given");
        request.Actions.Should().Equal("A10");
        request.Trigger.Should().Be(RunTrigger.Manual);
        Runner.Commands.Should().Equal($"systemctl start --no-block {answer.Unit}");
        History().Should().BeEmpty("nothing ran here: the unit runs it");
    }

    [Fact]
    public void A_detached_collect_is_accepted_the_same_way()
    {
        var (exit, stdout, stderr) = CliRun.Over(Host(), "collect", "--detach", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        HandOff(stdout).Kind.Should().Be("collect");
        Requests().Should().ContainSingle().Which.Should().BeOfType<RunRequestRead.Parsed>().Which.File.Actions.Should().Equal("collect");
    }

    [Fact]
    public void Without_systemd_a_detach_is_refused_with_69_and_never_falls_back_to_a_synchronous_run()
    {
        Directory.Delete(Sandbox.Paths.DistroPath("/run/systemd/system"));

        var (exit, stdout, stderr) = CliRun.Over(Host(), "act", "A10", "--confirm", "--detach");

        exit.Should().Be((int)ExitCode.DetachUnavailable);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("needs systemd").And.Contain("no synchronous fallback");
        Requests().Should().BeEmpty();
        History().Should().BeEmpty("nothing ran");
        Runner.Requests.Should().BeEmpty("not even the journal's preview");
    }

    [Fact]
    public void A_detach_while_a_run_is_queued_is_busy_and_writes_nothing()
    {
        // Within its grace: the unit may not have taken it yet (E6.S1 review D1 — an older one would be swept first).
        var queued = Plant("act", ["A9"], TimeSpan.FromSeconds(10));

        var (exit, _, stderr) = CliRun.Over(Host(), "act", "A10", "--confirm", "--detach");

        exit.Should().Be((int)ExitCode.Busy);
        stderr.Should().Contain("busy:").And.Contain(queued.RunId.Text);
        Requests().Should().ContainSingle();
        Starts().Should().BeEmpty();
    }

    [Fact]
    public void A_detach_meeting_a_wedged_run_is_refused_with_76()
    {
        PlantWedged(RunId.New(Now.AddMinutes(-9), 999), pid: 999);

        var (exit, _, stderr) = CliRun.Over(Host() with { Processes = WedgedTable(999) }, "act", "A10", "--confirm", "--detach");

        exit.Should().Be((int)ExitCode.Wedged, stderr);
        Requests().Should().BeEmpty();
    }

    [Fact]
    public void A_detach_meeting_an_unreadable_running_json_is_refused_with_79()
    {
        Directory.CreateDirectory(Sandbox.Paths.StateDirectory);
        File.WriteAllText(RunningState.File(Sandbox.Paths), "{}");

        var (exit, _, stderr) = CliRun.Over(Host(), "act", "A10", "--confirm", "--detach");

        exit.Should().Be((int)ExitCode.StateUnreadable, stderr);
        Starts().Should().BeEmpty();
    }

    /// <summary>E6.S1 review D5: an unusable request used to hold the state unreadable and refuse every detach (79) forever — the
    /// detach's own sweep now records it refused, removes it, and goes ahead.</summary>
    [Fact]
    public void A_detach_records_an_unusable_request_refused_removes_it_and_is_accepted()
    {
        var bad = RunId.New(Now, 5);
        var path = RunRequests.File(Sandbox.Paths, bad);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");

        var (exit, stdout, stderr) = CliRun.Over(Host(), "act", "A10", "--confirm", "--detach", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        HandOff(stdout).Result.Should().Be("accepted");
        History().Should().ContainSingle().Which.Should().Match<RunRecord>(r => r.RunId == bad && r.Outcome == RunOutcome.Refused);
        File.Exists(path).Should().BeFalse();
    }

    /// <summary>E6.S1 review D1: an ORPHANED request — its unit gone (a distro stop dropped the job, a detach killed before its
    /// start) — blocked every detach, the panel's only remedy, until a root collect swept it hours later. The detach sweeps it
    /// itself, under the lock, once its grace is over.</summary>
    [Fact]
    public void A_detach_sweeps_an_orphaned_request_whose_unit_is_gone_and_is_accepted()
    {
        var orphan = Plant("act", ["A9"], TimeSpan.FromMinutes(2), pid: 60);
        Runner.Script(UnitCommands.Show(orphan.RunId).Argv, 0, "ActiveState=inactive\nJob=\n");

        var (exit, stdout, stderr) = CliRun.Over(Host(), "act", "A10", "--confirm", "--detach", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var answer = HandOff(stdout);
        History().Should().ContainSingle().Which.Should().Match<RunRecord>(r => r.RunId == orphan.RunId && r.Outcome == RunOutcome.Interrupted);
        Requests().Should().ContainSingle().Which.Should().BeOfType<RunRequestRead.Parsed>().Which.File.RunId.Text.Should().Be(answer.RunId);
    }

    [Fact]
    public void A_detach_while_another_run_holds_the_lock_is_busy_or_wedged_and_writes_nothing()
    {
        var held = (ExclusiveLock.Held)RunLock.TryTake(Sandbox.Paths, Sandbox.Files);
        int busy, wedged;
        string busyErr;
        using (held.Handle)
        {
            (busy, _, busyErr) = CliRun.Over(Host(), "act", "A10", "--confirm", "--detach");
            PlantWedged(RunId.New(Now.AddMinutes(-9), 999), pid: 999);
            (wedged, _, _) = CliRun.Over(Host() with { Processes = WedgedTable(999) }, "act", "A10", "--confirm", "--detach");
        }

        busy.Should().Be((int)ExitCode.Busy);
        busyErr.Should().Contain("another run holds the lock");
        wedged.Should().Be((int)ExitCode.Wedged);
        Requests().Should().BeEmpty();
        Starts().Should().BeEmpty();
    }

    /// <summary>E6.S1 review D2: a signal while <c>act --request</c> sweeps (before its running.json) removed the request bare —
    /// no line, no running.json, no request: <c>runs show</c> answered unknown for a run the panel was told was accepted.</summary>
    [Fact]
    public void A_request_cut_off_before_it_started_keeps_one_interrupted_line()
    {
        var stale = Plant("act", ["A9"], TimeSpan.FromMinutes(30), pid: 61);
        var request = Plant("act", ["A10"], TimeSpan.FromSeconds(5), pid: 62);
        using var cancel = new CancellationTokenSource();
        Runner.ScriptEffect(argv => argv.SequenceEqual(UnitCommands.Show(stale.RunId).Argv), _ =>
        {
            cancel.Cancel();
            throw new OperationCanceledException(cancel.Token);
        });

        var run = () => CliRun.Over(Host(), Serilog.Core.Logger.None, cancel.Token, "act", "--request", request.RunId.Text);

        run.Should().Throw<OperationCanceledException>("the cancellation flies on (Main answers 130)");
        var line = History().Should().ContainSingle(r => r.RunId == request.RunId).Subject;
        line.Outcome.Should().Be(RunOutcome.Interrupted);
        line.Reason.Should().Contain("cut off before it started");
        File.Exists(RunRequests.File(Sandbox.Paths, request.RunId)).Should().BeFalse();
    }

    /// <summary>E6.S1 review D6: a start that TIMED OUT may have queued the job — the unit is asked before anything is removed.</summary>
    [Theory]
    [InlineData("ActiveState=inactive\nJob=17\n", 0, (int)ExitCode.Ok, "accepted", true)]
    [InlineData("ActiveState=inactive\nJob=\n", 0, (int)ExitCode.DetachStartFailed, "", false)]
    [InlineData("", 1, (int)ExitCode.Ok, "unknown", true)]
    public void A_timed_out_start_asks_the_unit_before_it_removes_the_request(string show, int showExit, int expected, string result, bool kept)
    {
        Runner.Script(argv => argv is ["systemctl", "start", ..], new CommandOutcome.TimedOut(CapturedText.Empty, CapturedText.Empty, UnitCommands.StartCeiling));
        Runner.Script(argv => argv is ["systemctl", "show", ..], RecordingCommandRunner.Exited(showExit, show, showExit == 0 ? string.Empty : "Failed to connect to bus"));

        var (exit, stdout, stderr) = CliRun.Over(Host(), "act", "A10", "--confirm", "--detach", "--json");

        exit.Should().Be(expected, stderr);
        if (result.Length > 0)
        {
            HandOff(stdout).Result.Should().Be(result);
        }

        Requests().Should().HaveCount(kept ? 1 : 0);
    }

    [Fact]
    public void A_full_request_folder_is_refused_with_73_naming_the_budget()

    {
        for (var pid = 1; pid <= RunRequests.MaxQueued; pid++)
        {
            Plant("act", ["A9"], TimeSpan.FromSeconds(10), pid);
        }

        var (exit, _, stderr) = CliRun.Over(Host(), "act", "A10", "--confirm", "--detach");

        exit.Should().Be((int)ExitCode.QueueFull);
        stderr.Should().Contain($"already holds {RunRequests.MaxQueued} requests");
        Requests().Should().HaveCount(RunRequests.MaxQueued);
    }

    [Fact]
    public void A_unit_that_will_not_start_takes_its_request_with_it_and_exits_71()
    {
        Runner.Script(argv => argv is ["systemctl", "start", ..], RecordingCommandRunner.Exited(5, stderr: "Unit wsl-care-act@.service not found."));

        var (exit, stdout, stderr) = CliRun.Over(Host(), "act", "A10", "--confirm", "--detach", "--json");

        exit.Should().Be((int)ExitCode.DetachStartFailed);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("did not succeed").And.Contain("not found").And.Contain("the request was removed");
        Requests().Should().BeEmpty("no orphaned request (plan §15k #1)");
    }

    [Fact]
    public void An_unprivileged_detach_is_refused_before_anything_is_written()
    {
        var (actExit, _, _) = CliRun.Over(Host(NotRoot), "act", "A10", "--confirm", "--detach");
        var (collectExit, _, collectErr) = CliRun.Over(Host(NotRoot), "collect", "--detach");

        actExit.Should().Be((int)ExitCode.NeedsRoot);
        collectExit.Should().Be((int)ExitCode.NeedsRoot);
        collectErr.Should().Contain("collect --detach needs root");
        Requests().Should().BeEmpty();
        Runner.Requests.Should().BeEmpty();
    }

    // ---------- --only - (stdin) ----------

    [Fact]
    public void Ten_thousand_names_on_stdin_reach_the_request_as_the_shown_list()
    {
        var (exit, _, stderr) = CliRun.Over(Host(stdin: Names(ShownList.MaxNames)), "act", "A4", "--confirm", "--detach", "--only", "-");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        Requests().Should().ContainSingle().Which.Should().BeOfType<RunRequestRead.Parsed>().Which.File.Shown.Should().HaveCount(ShownList.MaxNames);
    }

    [Theory]
    [InlineData(ShownList.MaxNames + 1, "more than")]
    [InlineData(-1, "larger than 1048576 bytes")]
    public void Stdin_past_the_count_or_the_byte_cap_is_refused_and_nothing_is_written(int names, string reason)
    {
        var stdin = names > 0 ? Names(names) : new string('a', 1024 * 1024 + 1);

        var (exit, stdout, stderr) = CliRun.Over(Host(stdin: stdin), "act", "A4", "--confirm", "--detach", "--only", "-");

        exit.Should().Be((int)ExitCode.Usage);
        stdout.Should().BeEmpty();
        stderr.Should().Contain(reason);
        Requests().Should().BeEmpty();
        Runner.Requests.Should().BeEmpty();
    }

    [Fact]
    public void A_bad_line_on_stdin_is_refused_by_its_number_never_echoed()
    {
        var (exit, _, stderr) = CliRun.Over(Host(stdin: Names(3) + "secret-looking text\n"), "act", "A4", "--confirm", "--detach", "--only", "-");

        exit.Should().Be((int)ExitCode.Usage);
        stderr.Should().Contain("line 4").And.NotContain("secret-looking");
        Requests().Should().BeEmpty();
    }

    [Fact]
    public void Stdin_with_no_end_within_the_ceiling_is_refused_never_waited_on()
    {
        using var never = new NeverEndingStream();
        var host = Host() with { StandardInput = () => never, StdinCeiling = TimeSpan.FromMilliseconds(300) };

        var started = DateTime.UtcNow;
        var (exit, _, stderr) = CliRun.Over(host, "act", "A4", "--confirm", "--detach", "--only", "-");

        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(10));
        exit.Should().Be((int)ExitCode.Usage);
        stderr.Should().Contain("had no end within 0.3 s");
        Requests().Should().BeEmpty();
    }

    // ---------- act --request ----------

    [Fact]
    public void A_request_runs_under_its_own_run_id_and_is_gone_once_the_run_is_recorded()
    {
        var request = Plant("act", ["A10"], TimeSpan.FromSeconds(5));

        var (exit, stdout, stderr) = CliRun.Over(Host(), "act", "--request", request.RunId.Text);

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var report = JsonSerializer.Deserialize(stdout, WslCareJsonContext.Default.ActReport)!;
        report.Result.Should().Be("recorded");
        History().Should().ContainSingle().Which.Should().Match<RunRecord>(r => r.RunId == request.RunId && r.Trigger == RunTrigger.Manual);
        Requests().Should().BeEmpty();
        File.Exists(RunningState.File(Sandbox.Paths)).Should().BeFalse();
    }

    /// <summary>States move request → running.json → history line, never with a gap (E6.S0 review round): while the run acts,
    /// its running.json stands and its request is already gone.</summary>
    [Fact]
    public void While_a_request_s_run_acts_its_running_json_stands_and_its_request_is_already_gone()
    {
        var request = Plant("act", ["A10"], TimeSpan.FromSeconds(5));
        var (requestThere, runningThere) = (true, false);
        Runner.ScriptEffect(argv => argv is ["journalctl", "--vacuum-time=30d"], _ =>
        {
            requestThere = File.Exists(RunRequests.File(Sandbox.Paths, request.RunId));
            runningThere = File.Exists(RunningState.File(Sandbox.Paths));
            return RecordingCommandRunner.Exited(0);
        });

        var (exit, _, stderr) = CliRun.Over(Host(), "act", "--request", request.RunId.Text);

        exit.Should().Be((int)ExitCode.Ok, stderr);
        runningThere.Should().BeTrue();
        requestThere.Should().BeFalse();
    }

    [Fact]
    public void A_collect_request_records_a_full_run_under_its_run_id()
    {
        var request = Plant("collect", ["collect"], TimeSpan.FromSeconds(5));

        var (exit, _, stderr) = CliRun.Over(Host(), "act", "--request", request.RunId.Text);

        exit.Should().Be((int)ExitCode.Ok, stderr);
        History().Should().ContainSingle().Which.RunId.Should().Be(request.RunId);
        Requests().Should().BeEmpty();
    }

    [Fact]
    public void A_request_that_meets_the_lock_is_recorded_refused_and_removed_never_a_silent_busy()
    {
        var request = Plant("act", ["A10"], TimeSpan.FromSeconds(5));
        var held = (ExclusiveLock.Held)RunLock.TryTake(Sandbox.Paths, Sandbox.Files);
        int exit;
        string stderr;
        using (held.Handle)
        {
            (exit, _, stderr) = CliRun.Over(Host(), "act", "--request", request.RunId.Text);
        }

        exit.Should().Be((int)ExitCode.Busy);
        stderr.Should().Contain("refused (recorded)");
        var line = History().Should().ContainSingle().Subject;
        line.RunId.Should().Be(request.RunId);
        line.Outcome.Should().Be(RunOutcome.Refused);
        line.Reason.Should().Contain("busy:");
        line.Actions.Should().ContainSingle().Which.Status.Should().Be(ActionStatus.Refused);
        Requests().Should().BeEmpty("every terminal path removes its request");
    }

    [Fact]
    public void A_request_under_an_invalid_configuration_is_recorded_refused_as_observe_only()
    {
        Sandbox.Write("/home/me/.config/wsl-care/config.json", "{ \"journal\": { \"keepDays\": 0 } }");
        var request = Plant("act", ["A10"], TimeSpan.FromSeconds(5));

        var (exit, _, _) = CliRun.Over(Host(), "act", "--request", request.RunId.Text);

        exit.Should().Be((int)ExitCode.ObserveOnly);
        History().Should().ContainSingle().Which.Outcome.Should().Be(RunOutcome.Refused);
        Requests().Should().BeEmpty();
    }

    /// <summary>History first, for the run's OWN request too (§15k #2): a run that recorded itself and died before removing its
    /// request is never run a second time — no second terminal line under one run id.</summary>
    [Fact]
    public void A_request_whose_run_already_recorded_itself_is_removed_and_never_run_twice()
    {
        var request = Plant("act", ["A10"], TimeSpan.FromSeconds(5));
        new RunRecordWriter(Sandbox.Paths, Sandbox.Files).Append(new RunRecord(1, request.RunId, RunTrigger.Manual, Now, Now, RunOutcome.Completed, [], null));

        var (exit, _, stderr) = CliRun.Over(Host(), "act", "--request", request.RunId.Text);

        exit.Should().Be((int)ExitCode.RequestGone);
        stderr.Should().Contain("already recorded itself");
        History().Should().ContainSingle("one run id, one terminal line");
        Requests().Should().BeEmpty();
        Runner.Commands.Should().NotContain(c => c.Contains("--vacuum-time", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_request_is_a_named_no_op_with_80_and_no_history_line()
    {
        var (exit, stdout, stderr) = CliRun.Over(Host(), "act", "--request", "20261004T120000Z-1");

        exit.Should().Be((int)ExitCode.RequestGone);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("no request names run 20261004T120000Z-1");
        History().Should().BeEmpty();
        Runner.Requests.Should().BeEmpty();
    }

    [Fact]
    public void A_request_whose_content_is_not_what_root_writes_is_refused_and_nothing_runs()
    {
        var runId = RunId.New(Now, 6);
        var path = RunRequests.File(Sandbox.Paths, runId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""{"schemaVersion":1,"runId":"{{runId}}","kind":"act","actions":["A4"],"trigger":"manual","createdAt":"2026-10-02T12:00:00+00:00","shown":["../../etc/passwd"]}""");

        var (exit, _, stderr) = CliRun.Over(Host(), "act", "--request", runId.Text);

        exit.Should().Be((int)ExitCode.Usage);
        stderr.Should().Contain("cannot be used").And.Contain("shown");
        History().Should().ContainSingle().Which.Should().Match<RunRecord>(r => r.RunId == runId && r.Outcome == RunOutcome.Refused, "E6.S1 review D5: recorded, never left to block every detach");
        File.Exists(path).Should().BeFalse();
        Runner.Requests.Should().BeEmpty();
    }

    [Fact]
    public void A_planted_group_writable_request_is_refused_by_the_hardened_reader()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("mode bits are the distro's: run in WSL or on the Linux legs");
            return;
        }

        var request = Plant("act", ["A10"], TimeSpan.FromSeconds(5));
        File.SetUnixFileMode(RunRequests.File(Sandbox.Paths, request.RunId), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead);

        var (exit, _, stderr) = CliRun.Over(Host(), "act", "--request", request.RunId.Text);

        exit.Should().Be((int)ExitCode.Usage);
        stderr.Should().Contain("cannot be used");
        History().Should().ContainSingle().Which.Outcome.Should().Be(RunOutcome.Refused, "nothing ran; the refusal is recorded and the request removed");
        File.Exists(RunRequests.File(Sandbox.Paths, request.RunId)).Should().BeFalse();
    }

    [Fact]
    public void A_request_sweeps_another_stale_request_whose_unit_is_gone_before_it_runs()
    {
        var stale = Plant("act", ["A9"], TimeSpan.FromMinutes(30), pid: 70);
        Runner.Script(UnitCommands.Show(stale.RunId).Argv, 0, "ActiveState=inactive\nJob=\n");
        var request = Plant("act", ["A10"], TimeSpan.FromSeconds(5), pid: 71);

        var (exit, _, stderr) = CliRun.Over(Host(), "act", "--request", request.RunId.Text);

        exit.Should().Be((int)ExitCode.Ok, stderr);
        History().Select(r => (r.RunId, r.Outcome)).Should().BeEquivalentTo([(stale.RunId, RunOutcome.Interrupted), (request.RunId, RunOutcome.Completed)]);
        Requests().Should().BeEmpty();
    }

    // ---------- act --stop ----------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_wedged_run_in_one_of_the_two_units_is_stopped_through_systemd_and_marked(bool timer)
    {
        var runId = RunId.New(Now.AddMinutes(-9), 999);
        var unit = timer ? UnitCommands.TimerService : SlotKind.ActUnit.Of(runId);
        PlantWedged(runId, pid: 999);
        Sandbox.Write("/proc/999/cgroup", $"0::{(timer ? RunStops.TimerCgroup : RunStops.ActSlice + unit)}\n");

        var (exit, stdout, stderr) = CliRun.Over(Host() with { Processes = WedgedTable(999) }, "act", "--stop", runId.Text, "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        HandOff(stdout).Should().Match<HandOffReport>(r => r.Result == "stopping" && r.Kind == "stop" && r.RunId == runId.Text && r.Unit == unit);
        Runner.Commands.Should().Equal($"systemctl stop {unit}");
        StopMarkers.List(Sandbox.Paths, Sandbox.Files).Should().Equal(runId);
    }

    [Fact]
    public void A_wedged_run_outside_the_two_units_is_never_stopped_and_its_pid_is_named()
    {
        var runId = RunId.New(Now.AddMinutes(-9), 999);
        PlantWedged(runId, pid: 999);
        Sandbox.Write("/proc/999/cgroup", "0::/user.slice/user-1000.slice/session-1.scope\n");

        var (exit, _, stderr) = CliRun.Over(Host() with { Processes = WedgedTable(999) }, "act", "--stop", runId.Text);

        exit.Should().Be((int)ExitCode.Usage);
        stderr.Should().Contain("not in the system's wsl-care.service nor in").And.Contain("session-1.scope").And.Contain("pid 999");
        Runner.Requests.Should().BeEmpty("nothing is killed, by pid or by unit");
        StopMarkers.List(Sandbox.Paths, Sandbox.Files).Should().BeEmpty();
    }

    /// <summary>E6.S1 review S3: only the last path component was compared — a user's own systemd --user unit named
    /// wsl-care.service (or wsl-care-act@&lt;runId&gt;.service) matched. The whole cgroup path is compared now.</summary>
    [Theory]
    [InlineData("0::/user.slice/user-1000.slice/user@1000.service/app.slice/wsl-care.service")]
    [InlineData("0::/user.slice/user-1000.slice/user@1000.service/app.slice/wsl-care-act@20261002T115100Z-999.service")]
    [InlineData("0::/system.slice/wsl-care-act@20261002T115100Z-999.service")]
    [InlineData("0::/system.slice/system-wsl\\x2dcare\\x2dact.slice/wsl-care-act@20261002T115100Z-1.service")]
    public void A_wedged_run_in_a_unit_that_only_ends_in_one_of_the_names_is_never_stopped(string cgroup)
    {
        var runId = RunId.New(Now.AddMinutes(-9), 999);
        PlantWedged(runId, pid: 999);
        Sandbox.Write("/proc/999/cgroup", cgroup + "\n");

        var (exit, _, stderr) = CliRun.Over(Host() with { Processes = WedgedTable(999) }, "act", "--stop", runId.Text);

        exit.Should().Be((int)ExitCode.Usage);
        stderr.Should().Contain("nothing was stopped");
        Runner.Requests.Should().BeEmpty();
    }

    [Fact]
    public void A_live_run_is_not_stopped_and_a_run_that_is_not_wedged_here_is_nothing_to_stop()
    {
        var runId = RunId.New(Now.AddMinutes(-1), 999);
        var live = new RunningFile(1, runId, RunTrigger.Manual, ["A10"], "A10", 999, Now.AddHours(-1), Now.AddMinutes(-1), Now.AddSeconds(-2), RunKind.Act);
        Directory.CreateDirectory(Sandbox.Paths.StateDirectory);
        File.WriteAllText(RunningState.File(Sandbox.Paths), JsonSerializer.Serialize(live, WslCareJsonContext.Default.RunningFile));

        var (liveExit, _, liveErr) = CliRun.Over(Host() with { Processes = WedgedTable(999) }, "act", "--stop", runId.Text);
        var (otherExit, _, otherErr) = CliRun.Over(Host() with { Processes = WedgedTable(999) }, "act", "--stop", "20261004T120000Z-5");

        liveExit.Should().Be((int)ExitCode.Busy);
        liveErr.Should().Contain("is acting, not wedged");
        otherExit.Should().Be((int)ExitCode.Usage);
        otherErr.Should().Contain("is not wedged here");
        Runner.Requests.Should().BeEmpty();
    }

    [Fact]
    public void A_stop_systemd_refuses_leaves_no_marker_behind()
    {
        var runId = RunId.New(Now.AddMinutes(-9), 999);
        PlantWedged(runId, pid: 999);
        Sandbox.Write("/proc/999/cgroup", "0::/system.slice/wsl-care.service\n");
        Runner.Script(argv => argv is ["systemctl", "stop", ..], RecordingCommandRunner.Exited(1, stderr: "Access denied"));

        var (exit, _, stderr) = CliRun.Over(Host() with { Processes = WedgedTable(999) }, "act", "--stop", runId.Text);

        exit.Should().Be((int)ExitCode.RunFailed);
        stderr.Should().Contain("Access denied");
        StopMarkers.List(Sandbox.Paths, Sandbox.Files).Should().BeEmpty();
    }

    private static FakeProcessTable WedgedTable(int pid) => new FakeProcessTable().Alive(pid, Now.AddHours(-1));

    private void PlantWedged(RunId runId, int pid)
    {
        var wedged = new RunningFile(1, runId, RunTrigger.Timer, ["A10"], "A10", pid, Now.AddHours(-1), Now.AddMinutes(-9), Now.AddMinutes(-5), RunKind.Act);
        Directory.CreateDirectory(Sandbox.Paths.StateDirectory);
        File.WriteAllText(RunningState.File(Sandbox.Paths), JsonSerializer.Serialize(wedged, WslCareJsonContext.Default.RunningFile));
    }

    /// <summary>A stdin whose writer never closes: every read blocks until the test ends.</summary>
    private sealed class NeverEndingStream : Stream
    {
        private readonly ManualResetEventSlim _released = new();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _released.Wait();
            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _released.Set();
            if (disposing)
            {
                _released.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
