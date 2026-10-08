using System.Globalization;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Archive;
using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Json;
using WslCare.Core.Processes;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Archive;

/// <summary>
/// Plan §15r D1, D8, E9.S4 — A13 in the engine: root decides WHEN, the target user's own process does every byte (the product's
/// own binary through the self-invocation templates); its preview is the child's, its trigger the backlog, its budget the run
/// limit's slack; the child's words are data — judged, bounded, and never a session's name in root's world-readable detail.
/// </summary>
public sealed partial class ArchiveActionTests : IDisposable
{
    internal const string Installed = "/opt/wsl-care/bin/wsl-care";
    private const string Boot = "6d1c1c5e-0000-4000-8000-0000000000a1";
    private const string Base = "/mnt/v/ai-archive";
    private const string SessionKey = "projects/p/s1.jsonl";
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly LinuxSandbox _sandbox = new("a13");
    private readonly ManualTimeProvider _clock = new(Now);
    private readonly List<ProcessEntry> _processes = [];

    public ArchiveActionTests() => _sandbox.Write("/proc/sys/kernel/random/boot_id", Boot + "\n");

    public void Dispose() => _sandbox.Dispose();

    private static (ConfigLayerFile, FileReadResult) Defaults() => (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults()));

    /// <summary>The machine layer (root's own keys) and the user layer naming the base.</summary>
    private static EffectiveConfig Config(string machine = "", bool withBase = true) =>
        ConfigLoader.Load([
            Defaults(),
            (new ConfigLayerFile(ConfigLayer.Machine, "/etc/wsl-care/config.json"), machine.Length == 0 ? new FileReadResult.Missing() : new FileReadResult.Content(Encoding.UTF8.GetBytes(machine))),
            (new ConfigLayerFile(ConfigLayer.User, "user.json"), new FileReadResult.Content(Encoding.UTF8.GetBytes(withBase ? $$"""{ "archive": { "baseFolder": "{{Base}}" } }""" : "{}"))),
        ]).Config;

    private ActionContext Context(EffectiveConfig? config = null, RunTrigger trigger = RunTrigger.Manual, IFileSystem? files = null) =>
        new(_sandbox.Paths, files ?? _sandbox.Files, _clock, config ?? Config(), trigger, new TargetUserResult.Found(UserWorldMe, "test"))
        {
            Processes = _ => Reading.Of(new ProcessSnapshot(_processes.Count, 0, 0, 0, 0, [.. _processes], [])),
            RunId = "20261006T120000Z-42",
            RunStarted = Now,
        };

    private static readonly TargetUser UserWorldMe = new("me", 1000, "/home/me");

    internal static ActionCommands Commands(ICleanupAction action, ICommandRunner runner, ActionContext context, Func<SelfBinaryResult>? self = null) =>
        new(action, runner, context.TargetUser, []) { Self = self ?? (() => new SelfBinaryResult.Found(Installed)) };

    /// <summary>The verb words after the binary in a recorded request (<c>archive preview --json</c>).</summary>
    private static string Verb(CommandRequest request) => string.Join(' ', request.Argv.Skip(5));

    private static bool Is(IReadOnlyList<string> argv, string verb) => argv.Count > 6 && argv[5] == "archive" && argv[6] == verb;

    // ---- the children's answers, in their real shapes ------------------------------------------------------------------------

    internal static string PreviewJson(int due = 3, long bytes = 3000, double oldestDaysAgo = 20, bool retentionKnown = true, string agent = "claude-code", int removalsDue = 0) =>
        JsonSerializer.Serialize(
            new ArchivePreviewReport(SchemaVersion.Current, "wsl", "wsl-host-distro", Now, "UTC", Base, new InUseReport("complete", 0, 0, string.Empty), [
                new AgentPreviewReport(agent, "Claude Code", true, "/home/me/.claude", new ArchiveRetentionReport("claude-settings", retentionKnown, 30, "the default", []), 14, due, due * 2, bytes, due > 0 ? Now.AddDays(-oldestDaysAgo) : null, 1, [], 0, string.Empty, [],
                    due > 0 ? [new ArchiveUnitReport("session", SessionKey, 2, 200, Now.AddDays(-oldestDaysAgo), "2026/09")] : []),
            ])
            { RemovalsDue = removalsDue },
            WslCareJsonContext.Compact.ArchivePreviewReport);

    internal static ArchiveRunReport RunReport(int copied = 2, long removedBytes = 4096, string outcome = RunOutcomes.Done, string agent = "claude-code") =>
        new(SchemaVersion.Current, "wsl", "wsl-host-distro", "20261006T120000Z-42", Now, Now, Base, outcome, string.Empty, ArchiveReconcileReport.Empty, new InUseReport("complete", 0, 0, string.Empty),
            [new AgentRunReport(agent, true, copied, copied * 2, 8192, 1, removedBytes, 0, 0, 0, 0, [new SkipCount("in-use", 1, SessionKey, "held open")], $"skipped {SessionKey}")], 2.5, 1.25, [$"a note naming {SessionKey} and /etc/shadow, pid 1"]);

    internal static string RunJson(ArchiveRunReport? report = null) => JsonSerializer.Serialize(report ?? RunReport(), WslCareJsonContext.Compact.ArchiveRunReport);

    private static string Progress(int files) => JsonSerializer.Serialize(new ArchiveProgressLine("file", files, files * 100L, files), WslCareJsonContext.Compact.ArchiveProgressLine);

    private static RecordingCommandRunner Children(string? preview = null, int reachExit = 0, IReadOnlyList<string>? runLines = null, CommandOutcome? runOutcome = null)
    {
        var runner = new RecordingCommandRunner()
            .Script(argv => Is(argv, "preview"), RecordingCommandRunner.Exited(0, preview ?? PreviewJson()))
            .Script(argv => Is(argv, "reach"), RecordingCommandRunner.Exited(reachExit, RunJson(RunReport(copied: 0, outcome: reachExit == 0 ? RunOutcomes.Done : RunOutcomes.Unreachable))));
        runner.Stream(new StreamScript(runLines ?? [Progress(1), Progress(2), RunJson()], runOutcome ?? RecordingCommandRunner.Exited(0)));
        return runner;
    }

    private async Task<(ActionPreview Preview, ActionRun Run, RecordingCommandRunner Runner)> PreviewAndRun(RecordingCommandRunner? runner = null, ActionContext? context = null)
    {
        var action = new ArchiveAction();
        var ctx = context ?? Context();
        var children = runner ?? Children();
        var commands = Commands(action, children, ctx);
        var preview = await action.PreviewAsync(ctx, commands, CancellationToken.None);
        var run = await action.RunAsync(ctx, preview, commands, CancellationToken.None);
        return (preview, run, children);
    }

    private async Task<ActionPreview> Previewed(RecordingCommandRunner? runner = null, ActionContext? context = null, Func<SelfBinaryResult>? self = null)
    {
        var action = new ArchiveAction();
        var ctx = context ?? Context();
        return await action.PreviewAsync(ctx, Commands(action, runner ?? Children(), ctx, self), CancellationToken.None);
    }

    // ---- D8: no base, no archive ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Without_a_base_folder_A13_skips_no_archive_configured_and_starts_nothing()
    {
        var runner = Children();

        var preview = await Previewed(runner, Context(Config(withBase: false)));

        preview.Skip.Should().Contain("no archive configured");
        runner.Requests.Should().BeEmpty();
    }

    // ---- D1: the user's own process does every byte ----------------------------------------------------------------------------

    /// <summary>R2: the ONLY commands A13 starts are the user children — <c>runuser -u me -- &lt;the installed binary&gt; archive …</c> —
    /// and root itself opens nothing under the agent's folder (it reads its own state, the boot id, the PAM stack).</summary>
    [Fact]
    public async Task The_timer_never_moves_a_session_as_root()
    {
        var reads = new ReadRecordingFiles(_sandbox.Files);
        var (_, run, runner) = await PreviewAndRun(context: Context(files: reads));

        run.Succeeded.Should().BeTrue(run.Failure);
        runner.Requests.Should().NotBeEmpty();
        runner.Requests.Should().OnlyContain(r => r.Argv.Count > 5 && r.Argv[0] == "runuser" && r.Argv[1] == "-u" && r.Argv[2] == "me" && r.Argv[3] == "--" && r.Argv[4] == Installed && r.Argv[5] == "archive");
        runner.Requests.Select(Verb).Should().Equal("archive preview --json", "archive reach --json", "archive run --budget-seconds 1800 --run-id 20261006T120000Z-42 --json");
        runner.Requests.Should().OnlyContain(r => r.Environment is CommandEnvironment.Clean && r.StdinClosed);
        reads.Paths.Should().NotContain(p => p.Contains(".claude", StringComparison.Ordinal) || p.Contains(Base, StringComparison.Ordinal), "root never opens a session file nor the base");
    }

    [Fact]
    public async Task A13_refuses_when_the_products_binary_is_not_roots_alone()
    {
        var runner = Children();

        var preview = await Previewed(runner, self: () => new SelfBinaryResult.Refused("the product's binary /opt/wsl-care/bin/wsl-care is not owned by root"));

        new[] { preview.Refusal, preview.Reason ?? string.Empty }.Should().Contain(r => r.Contains("not owned by root", StringComparison.Ordinal));
        runner.Requests.Should().BeEmpty("nothing is started when the binary is not root's");
    }

    // ---- the preview, the trigger, the urgency --------------------------------------------------------------------------------

    /// <summary>D8, §15q R9: the preview is the child's, in AGGREGATES — per agent the sessions due and their bytes — and names no
    /// session although the child's own answer does.</summary>
    [Fact]
    public async Task The_preview_is_the_childs_counted_per_agent_and_names_no_session()
    {
        var preview = await Previewed();

        preview.Available.Should().BeTrue(preview.Reason);
        preview.Count.Should().Be(3);
        preview.Bytes.Should().Be(3000);
        preview.Items.Should().ContainSingle().Which.Name.Should().Be("claude-code");
        JsonSerializer.Serialize(preview, WslCareJsonContext.Default.ActionPreview).Should().NotContain(SessionKey);
    }

    [Theory]
    [InlineData(3, 0, true)]
    [InlineData(0, 2, true)]
    [InlineData(0, 0, false)]
    public async Task The_trigger_fires_on_a_session_due_or_a_removal_due(int due, int removalsDue, bool fires)
    {
        var preview = await Previewed(Children(preview: PreviewJson(due: due, removalsDue: removalsDue)));

        new ArchiveAction().Trigger(preview, Config()).Fired.Should().Be(fires, preview.Reason);
    }

    /// <summary>D8: within <c>archive.urgentWithinDays</c> of the agent's own deletion the timer does not wait for idle.</summary>
    [Theory]
    [InlineData(25, true, true)]
    [InlineData(10, true, false)]
    [InlineData(25, false, false)]
    public async Task An_urgent_backlog_does_not_wait_for_idle(double oldestDaysAgo, bool retentionKnown, bool urgent)
    {
        var preview = await Previewed(Children(preview: PreviewJson(oldestDaysAgo: oldestDaysAgo, retentionKnown: retentionKnown)));

        (preview.Urgent.Length > 0).Should().Be(urgent, preview.Urgent);
        if (urgent)
        {
            preview.Urgent.Should().Contain("claude-code");
        }
    }

    // ---- the budget -----------------------------------------------------------------------------------------------------------

    /// <summary>D8: a timer run takes the run limit's SLACK — never more than is left until <c>timer.runLimitMinutes</c> after the
    /// actions behind it, never more than <c>archive.runBudgetMinutes</c>; below <c>archive.minRunMinutes</c> it does not start.</summary>
    [Fact]
    public async Task The_archive_budget_never_passes_the_run_limit()
    {
        var config = Config();
        var after = RunBudget.WorstCaseOf(ActionRegistry.Product.InExecutionOrder([.. ActionId.ExecutionOrder.SkipWhile(id => id.Text != "A13").Skip(1)]), config);
        var limit = TimeSpan.FromMinutes(config.Int(ConfigKeys.Timer.RunLimitMinutes));
        var leftMinutes = 12;
        var started = Now - limit + after + RunBudget.Margin + TimeSpan.FromMinutes(leftMinutes);
        var context = Context(config, RunTrigger.Timer) with { RunStarted = started };

        var (preview, run, runner) = await PreviewAndRun(context: context);

        run.Succeeded.Should().BeTrue(run.Failure + preview.Skip);
        var stream = runner.Requests.Single(r => Is(r.Argv, "run"));
        stream.Argv.SkipWhile(a => a != "--budget-seconds").Skip(1).First().Should().Be((leftMinutes * 60).ToString(CultureInfo.InvariantCulture));
        stream.Timeout.Should().Be(TimeSpan.FromMinutes(leftMinutes + config.Int(ConfigKeys.Archive.FinishGraceMinutes)));
    }

    [Fact]
    public async Task A_timer_run_with_less_slack_than_its_minimum_skips_no_time_left()
    {
        var config = Config();
        var context = Context(config, RunTrigger.Timer) with { RunStarted = Now - TimeSpan.FromMinutes(config.Int(ConfigKeys.Timer.RunLimitMinutes)) };
        var runner = Children();

        var preview = await Previewed(runner, context);

        preview.Skip.Should().Contain("no time left in this run");
        runner.Requests.Should().NotContain(r => Is(r.Argv, "run"));
    }

    // ---- the child's answer is data -------------------------------------------------------------------------------------------

    /// <summary>D8: a confirmed run's measured result — per agent, the counts and the bytes the removals freed — names no session and
    /// takes nothing of the child's free text (its notes, its first skipped key).</summary>
    [Fact]
    public async Task The_run_detail_names_no_session()
    {
        var (preview, run, _) = await PreviewAndRun();

        run.Succeeded.Should().BeTrue(run.Failure);
        run.FreedBytes.Should().Be(4096);
        run.Removed.Should().ContainSingle().Which.Name.Should().Be("claude-code");
        var detail = JsonSerializer.Serialize(new ActionOutcome("A13", "archive", ActionStatus.Ran, "ran", preview, run), WslCareJsonContext.Default.ActionOutcome);
        detail.Should().NotContain(SessionKey).And.NotContain("/etc/shadow");
    }

    public static TheoryData<string, string> MalformedAnswers => new()
    {
        { "not json", "{ this is not json" },
        { "another schema", RunJson(RunReport() with { SchemaVersion = 99 }) },
        { "a negative count", RunJson(RunReport(copied: -1)) },
        { "a negative removal count alone", RunJson(RunReport() with { Agents = [RunReport().Agents[0] with { Removed = -1 }] }) },
        { "an agent the archive does not move", RunJson(RunReport(agent: "../evil")) },
        { "an outcome the archive does not answer", RunJson(RunReport(outcome: "exploded")) },
        { "a negative rate", RunJson(RunReport() with { FilesPerSecond = -1 }) },
        { "a count past the session cap", RunJson(RunReport(copied: 1_000_000)) },
    };

    [Theory]
    [MemberData(nameof(MalformedAnswers))]
    public async Task A_malformed_child_answer_fails_A13_and_records_nothing_from_it(string why, string answer)
    {
        var (_, run, _) = await PreviewAndRun(Children(runLines: [Progress(1), answer]));

        run.Succeeded.Should().BeFalse(why);
        run.Count.Should().Be(0);
        run.FreedBytes.Should().BeNull();
        run.Removed.Should().BeEmpty();
    }

    /// <summary>9/9.4 #4: progress lines are counted and dropped, so a flood of them is no answer; a flood of OTHER lines passes the
    /// answer's cap and ends the child; a line as long as the runner's cut is never read as the start of a record.</summary>
    [Fact]
    public async Task A_flood_of_progress_lines_is_harmless_and_a_flood_of_anything_else_stops_the_child()
    {
        var progress = Enumerable.Range(1, 20_000).Select(Progress).Append(RunJson()).ToList();
        var (_, harmless, _) = await PreviewAndRun(Children(runLines: progress));
        var cap = Config().Int(ConfigKeys.Archive.ChildOutputCapBytes);
        var junk = Enumerable.Repeat("x", (cap / 2) + 10).Append(RunJson()).ToList();
        var (_, flooded, _) = await PreviewAndRun(Children(runLines: junk));

        harmless.Succeeded.Should().BeTrue(harmless.Failure);
        flooded.Succeeded.Should().BeFalse();
        flooded.Count.Should().Be(0);
    }

    [Fact]
    public async Task An_oversized_line_with_a_valid_looking_start_is_never_parsed()
    {
        var cut = Config().Int(ConfigKeys.Archive.ChildOutputCapBytes);
        var oversized = RunJson() + new string(' ', cut);

        var (_, run, _) = await PreviewAndRun(Children(runLines: [oversized[..cut]]));

        run.Succeeded.Should().BeFalse();
        run.Failure.Should().Contain("cut");
        run.Count.Should().Be(0);
    }

    // ---- containment ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_hung_archive_child_is_killed_at_its_ceiling_and_A13_fails_with_that_reason()
    {
        var (_, run, _) = await PreviewAndRun(Children(runLines: [Progress(1)], runOutcome: new CommandOutcome.TimedOut(CapturedText.Empty, CapturedText.Empty, TimeSpan.FromMinutes(35))));

        run.Succeeded.Should().BeFalse();
        run.Failure.Should().Contain("ceiling");
    }

    /// <summary>D1: the short child first — a base that does not answer stops A13 before the long one is started.</summary>
    [Fact]
    public async Task A_base_the_short_child_cannot_reach_stops_A13_before_the_long_run()
    {
        var (_, run, runner) = await PreviewAndRun(Children(reachExit: 1));

        run.Succeeded.Should().BeFalse();
        run.Failure.Should().Contain("did not answer");
        runner.Requests.Should().NotContain(r => Is(r.Argv, "run"));
    }

    /// <summary>9/9.4 #1: a recorded child of this boot still alive — stuck in the kernel on the base, most likely — keeps A13 from
    /// starting a second one; once it is gone the record is retired.</summary>
    [Fact]
    public async Task A_lock_holder_stuck_in_the_kernel_makes_A13_skip()
    {
        ArchiveChildren.Write(_sandbox.Paths, _sandbox.Files, new ArchiveChildFile(1, Boot, [new ArchiveChildIdentity(ArchiveChildren.Worker, "archive-run", 777, 5000, Now.AddHours(-5))])).Should().BeEmpty();
        _processes.Add(UserWorldProcess(777, 5000, 'D'));
        var runner = Children();

        var stuck = await Previewed(runner);
        _processes.Clear();
        var free = await Previewed(runner);

        stuck.Skip.Should().Contain("stuck in the kernel");
        free.Skip.Should().BeEmpty();
        ArchiveChildren.Read(_sandbox.Paths, _sandbox.Files).Children.Should().BeEmpty("a record whose children are gone is retired");
    }

    [Fact]
    public async Task A_recorded_identity_of_another_boot_or_another_process_is_not_a_survivor()
    {
        ArchiveChildren.Write(_sandbox.Paths, _sandbox.Files, new ArchiveChildFile(1, "an-earlier-boot", [new ArchiveChildIdentity(ArchiveChildren.Worker, "archive-run", 777, 5000, Now.AddHours(-5))])).Should().BeEmpty();
        _processes.Add(UserWorldProcess(777, 5000, 'D'));
        var otherBoot = await Previewed();
        ArchiveChildren.Write(_sandbox.Paths, _sandbox.Files, new ArchiveChildFile(1, Boot, [new ArchiveChildIdentity(ArchiveChildren.Worker, "archive-run", 777, 4000, Now.AddHours(-5))])).Should().BeEmpty();
        var otherProcess = await Previewed();

        otherBoot.Skip.Should().BeEmpty("the pid is another process after a reboot");
        otherProcess.Skip.Should().BeEmpty("the same pid with another start is another process");
    }

    /// <summary>9/9.4 #1, the plan round's finding 2: the launcher and the worker under it are recorded while the child runs, and the
    /// record is emptied once both are gone — never a growing list.</summary>
    [Fact]
    public async Task The_children_are_recorded_while_they_run_and_retired_when_gone()
    {
        var seen = new List<IReadOnlyList<ArchiveChildIdentity>>();
        var runner = Children(runLines: [Progress(1), Progress(2), RunJson()]);
        _processes.Add(UserWorldProcess(4242, 9000, 'S'));
        _processes.Add(UserWorldProcess(4243, 9001, 'S') with { ParentPid = 4242 });
        var context = Context();
        var action = new ArchiveAction();
        var commands = Commands(action, runner, context);
        var preview = await action.PreviewAsync(context, commands, CancellationToken.None);
        _ = ArchiveChildren.Read(_sandbox.Paths, _sandbox.Files);
        var observing = new ObservingStreams(runner, () => seen.Add(ArchiveChildren.Read(_sandbox.Paths, _sandbox.Files).Children), () => _processes.Clear());

        var run = await action.RunAsync(context, preview, Commands(action, observing, context), CancellationToken.None);

        run.Succeeded.Should().BeTrue(run.Failure);
        seen.Should().NotBeEmpty();
        seen[^1].Select(c => (c.Role, c.Pid, c.StartTicks)).Should().BeEquivalentTo([(ArchiveChildren.Launcher, 4242, 9000L), (ArchiveChildren.Worker, 4243, 9001L)]);
        ArchiveChildren.Read(_sandbox.Paths, _sandbox.Files).Children.Should().BeEmpty();
    }

    /// <summary>9/9.4 #3: whatever a child says — a pid, a cgroup, a root-only path — root signals nothing and opens nothing for it.</summary>
    [Fact]
    public async Task Foreign_identities_in_the_childs_answer_are_never_signalled_nor_opened()
    {
        var signals = new CountingSignals();
        var reads = new ReadRecordingFiles(_sandbox.Files);
        var context = Context(files: reads) with { Signals = signals };

        var (_, run, _) = await PreviewAndRun(context: context);

        run.Succeeded.Should().BeTrue(run.Failure);
        signals.Asked.Should().Be(0);
        reads.Paths.Should().NotContain(p => p.Contains("shadow", StringComparison.Ordinal));
    }

    // ---- 9/9.4 #2 -------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("session optional pam_systemd.so\n", "", "/etc/pam.d/runuser names pam_systemd")]
    [InlineData("@include common-session\n", "session optional pam_systemd.so\n", "/etc/pam.d/common-session names pam_systemd")]
    public async Task A13_refuses_where_runusers_pam_stack_would_make_a_login_session(string runuser, string included, string reason)
    {
        _sandbox.Write("/etc/pam.d/runuser", runuser);
        if (included.Length > 0)
        {
            _sandbox.Write("/etc/pam.d/common-session", included);
        }

        var runner = Children();
        var preview = await Previewed(runner);

        preview.Refusal.Should().Contain(reason);
        runner.Requests.Should().BeEmpty();
    }

    /// <summary>The S4 code round, finding 3: an include of an include is followed (each file once, so a loop ends), and a file the stack
    /// pulls in that cannot be read refuses — it could name pam_systemd for all root knows.</summary>
    [Theory]
    [InlineData("nested", "/etc/pam.d/common-inner")]
    [InlineData("missing", "/etc/pam.d/common-missing could not be checked")]
    [InlineData("loop", "")]
    public async Task A13_follows_every_file_the_pam_stack_pulls_in(string shape, string reason)
    {
        _sandbox.Write("/etc/pam.d/runuser", shape == "missing" ? "@include common-missing\n" : "@include common-outer\n");
        _sandbox.Write("/etc/pam.d/common-outer", "session required pam_unix.so\n@include common-inner\n");
        _sandbox.Write("/etc/pam.d/common-inner", shape == "loop" ? "session include common-outer\n" : "session optional pam_systemd.so\n");

        var preview = await Previewed();

        if (reason.Length == 0)
        {
            preview.Refusal.Should().BeEmpty("a loop of includes naming no pam_systemd ends and refuses nothing");
        }
        else
        {
            preview.Refusal.Should().Contain(reason);
        }
    }

    [Fact]
    public async Task A_pam_stack_without_pam_systemd_lets_A13_run()
    {
        _sandbox.Write("/etc/pam.d/runuser", "auth sufficient pam_rootok.so\nsession optional pam_keyinit.so revoke\nsession required pam_limits.so\nsession required pam_unix.so\n");

        var preview = await Previewed();

        preview.Refusal.Should().BeEmpty();
    }

    private static ProcessEntry UserWorldProcess(int pid, long start, char state) =>
        new(pid, 1, "me", "wsl-care", state, 0, 0, Reading.Of(TimeSpan.FromHours(1)), Reading.Of(0.1), Reading.Of("/"), "wsl-care archive run", "other", false, false, false) { StartTicks = Reading.Of(start) };
}
