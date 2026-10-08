using System.Globalization;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Actions.Suspects;
using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;
using WslCare.Core.Mcp;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions;

/// <summary>
/// A19 (plan E14 S2a, the owner's decision of 2026-10-08): stop the target user's IDLE MCP servers — no CPU for
/// <c>mcpWatchdog.idleMinutes</c> measured by identity over the timer's CPU history (the one A18 uses), an orphaned one after
/// <c>mcpWatchdog.orphanIdleMinutes</c>; by pid AND start through the shared signal path; a button bound to what its modal showed,
/// and automatic (on by default) under the daemon's dry-run rules.
/// </summary>
public sealed class McpServerStopTests : IDisposable
{
    private const string Boot = "6d1c1c5e-0000-4000-8000-000000000001";
    private const string Coai = "/home/me/.vscode-server/data/User/globalStorage/remsoftdev.connect-other-ais/coai-mcp";
    private const long Start = 4000;

    private readonly LinuxSandbox _sandbox = new("a19");
    private readonly ManualTimeProvider _clock = new(FixedTimeProvider.DefaultNow) { SteppedTimestamps = true };

    public McpServerStopTests() => _sandbox.Write("/proc/sys/kernel/random/boot_id", Boot + "\n");

    public void Dispose() => _sandbox.Dispose();

    private sealed class RecordingSignals : IProcessSignals
    {
        public List<ProcessIdentity> Asked { get; } = [];

        public Task<IReadOnlyList<SignalOutcome>> TerminateAllAsync(IReadOnlyList<ProcessIdentity> processes, TimeSpan grace, CancellationToken cancellationToken)
        {
            Asked.AddRange(processes);
            return Task.FromResult<IReadOnlyList<SignalOutcome>>([.. processes.Select(_ => new SignalOutcome.Ended(NeededKill: false))]);
        }
    }

    /// <summary>One process's <c>stat</c> and <c>status</c> as /proc answers them.</summary>
    private void Stat(int pid, long cpuTicks, int parent = 200, long start = Start, int tty = 0, int uid = 1000, string name = "coai-mcp")
    {
        _sandbox.Write($"/proc/{pid}/stat", string.Create(CultureInfo.InvariantCulture, $"{pid} ({name}) S {parent} {pid} {pid} {tty} -1 0 0 0 0 0 {cpuTicks} 0 0 0 20 0 1 0 {start} 0 0\n"));
        _sandbox.Write($"/proc/{pid}/status", string.Create(CultureInfo.InvariantCulture, $"Name:\t{name}\nState:\tS (sleeping)\nPPid:\t{parent}\nUid:\t{uid}\t{uid}\t{uid}\t{uid}\nRssAnon:\t30000 kB\nRssShmem:\t0 kB\n"));
    }

    /// <summary>The Claude Code session the servers run under (an AI agent of the target user).</summary>
    private static ProcessEntry Agent(int pid = 200) =>
        UserWorld.Process(pid, "/home/me/.local/bin/claude --resume", cwd: "/home/me/git/p", family: ProcessFamilies.AiAgents, ageHours: 48) with { StartTicks = Reading.Of(Start) };

    /// <summary>A coai-mcp server under <paramref name="parent"/> (pid 1 = re-parented to init).</summary>
    private static ProcessEntry Server(int pid = 300, int parent = 200, string user = "me", bool tty = false, long start = Start, bool orphaned = false) =>
        UserWorld.Process(pid, Coai, family: "vscode-server", user: user, tty: tty, ageHours: 6, orphaned: orphaned) with { ParentPid = parent, StartTicks = Reading.Of(start) };

    private ActionContext Context(IReadOnlyList<ProcessEntry> processes, IProcessSignals? signals = null, RunTrigger trigger = RunTrigger.Cli, ShownList? shown = null)
    {
        var config = ConfigLoader.Load(_sandbox.Paths, _sandbox.Files).Config;
        return new ActionContext(_sandbox.Paths, _sandbox.Files, _clock, config, trigger, new TargetUserResult.Found(new TargetUser("me", 1000, "/home/me"), "test"))
        {
            Processes = _ => Reading.Of(UserWorld.Snapshot(processes)),
            Signals = signals ?? new RecordingSignals(),
            ShownProcesses = shown ?? ShownList.None,
        };
    }

    private async Task<ActionPreview> Preview(IReadOnlyList<ProcessEntry> processes, RunTrigger trigger = RunTrigger.Cli, ShownList? shown = null)
    {
        var action = new McpServerStop();
        var context = Context(processes, trigger: trigger, shown: shown);
        return await action.PreviewAsync(context, new ActionCommands(action, new RecordingCommandRunner(), context.TargetUser, []), CancellationToken.None);
    }

    /// <summary>What every timer run does: record the CPU ticks of the AI-agent processes AND the WATCHED MCP servers (the
    /// configuration's, as <c>ActionEngine</c> passes them) by identity.</summary>
    private void Record(IReadOnlyList<ProcessEntry> processes) =>
        AgentCpuHistory.Record(_sandbox.Paths, _sandbox.Files, processes, SampleTime.Of(_clock), McpSettings.From(ConfigLoader.Load(_sandbox.Paths, _sandbox.Files).Config).Watched).Should().BeEmpty();

    private const string CredsPath = "/home/me/.local/bin/creds-mcp";

    /// <summary>A user-added MCP program (<c>mcpServers.programs</c>, plan E14 S2c) under <paramref name="parent"/>.</summary>
    private static ProcessEntry Creds(int pid, int parent = 200, string user = "me", bool orphaned = false) =>
        UserWorld.Process(pid, CredsPath, user: user, ageHours: 6, orphaned: orphaned) with { ParentPid = parent, StartTicks = Reading.Of(Start) };

    private void Programs(string json) => _sandbox.Write("/etc/wsl-care/config.json", $"{{ \"mcpServers\": {{ \"programs\": {json} }} }}");

    [Fact]
    public async Task A_user_program_is_watched_measured_and_stopped_when_idle_like_a_catalogue_server()
    {
        Stat(200, cpuTicks: 9000, parent: 100, name: "claude");
        Stat(330, cpuTicks: 500, name: "creds-mcp");
        var processes = new[] { Agent(), Creds(330) };

        var unlisted = await IdleFor(TimeSpan.FromHours(2), processes);
        Programs("[\"creds-mcp\"]");
        var listed = await IdleFor(TimeSpan.FromHours(2), processes);

        unlisted.Count.Should().Be(0, "a program nobody listed is no MCP server");
        var target = listed.Targets.Should().ContainSingle().Subject;
        target.Key.Should().Be("330:4000:500:1000", "the same identity the signal path re-checks as for a catalogue server");
        target.Note.Should().Contain("creds-mcp").And.Contain("Claude Code").And.Contain("/mcp");
        McpSettings.From(ConfigLoader.Load(_sandbox.Paths, _sandbox.Files).Config).Watched.Should().Contain(s => s.Name == "creds-mcp")
            .Which.Logs.Should().BeOfType<McpLogLayout.None>("its log layout is unknown: starts are the live-younger lower bound");
    }

    [Fact]
    public async Task Another_users_process_of_a_user_program_is_never_stopped()
    {
        Programs("[\"creds-mcp\"]");
        Stat(200, cpuTicks: 9000, parent: 100, name: "claude");
        Stat(330, cpuTicks: 500, uid: 1001, name: "creds-mcp");
        Stat(340, cpuTicks: 500, uid: 0, name: "creds-mcp");

        var preview = await IdleFor(TimeSpan.FromHours(2), [Agent(), Creds(330, user: "other"), Creds(340, user: "root")]);

        preview.Count.Should().Be(0, "a name a user adds lets root stop only that user's own processes");
    }

    [Fact]
    public async Task An_orphaned_instance_of_a_user_program_is_never_stopped()
    {
        // coai plan round 2026-10-08 (session 563a1e95): a user-chosen file name is not as specific as a catalogue server's, so
        // once no agent holds the process it may be an unrelated program of the same name.
        Programs("[\"creds-mcp\"]");
        Stat(300, cpuTicks: 500, parent: 1);
        Stat(330, cpuTicks: 500, parent: 1, name: "creds-mcp");
        Stat(340, cpuTicks: 500, parent: 900, name: "creds-mcp");

        var preview = await IdleFor(TimeSpan.FromHours(2), [Server(300, parent: 1, orphaned: true), Creds(330, parent: 1, orphaned: true), Creds(340, parent: 900, orphaned: true)]);

        preview.Targets.Select(t => SuspectSignals.Shown(t.Key)).Should().Equal(["300:4000"], "the catalogue server's orphan is still a target");
        preview.Basis.Should().Contain($"2 because {McpServerStop.UserProgramOrphan}");
    }

    /// <summary>The server idle across two timer sightings <paramref name="apart"/> apart.</summary>
    private async Task<ActionPreview> IdleFor(TimeSpan apart, IReadOnlyList<ProcessEntry> processes)
    {
        Record(processes);
        _clock.Advance(apart);
        Record(processes);
        return await Preview(processes);
    }

    [Fact]
    public async Task An_mcp_server_idle_for_longer_than_the_key_is_a_target_by_pid_and_start()
    {
        Stat(200, cpuTicks: 9000, parent: 100, name: "claude");
        Stat(300, cpuTicks: 500);

        var early = await IdleFor(TimeSpan.FromMinutes(30), [Agent(), Server()]);
        _clock.Advance(TimeSpan.FromMinutes(31));
        var late = await Preview([Agent(), Server()]);

        early.Count.Should().Be(0, "30 minutes without CPU is under the default 60");
        var target = late.Targets.Should().ContainSingle().Subject;
        target.Key.Should().Be("300:4000:500:1000", "the identity the signal path re-checks: pid, start ticks, CPU ticks, uid");
        target.Note.Should().Contain("coai-mcp").And.Contain("Claude Code").And.Contain("/mcp");
    }

    [Fact]
    public async Task An_mcp_server_that_used_cpu_within_the_window_or_has_no_history_is_kept()
    {
        Stat(200, cpuTicks: 9000, parent: 100, name: "claude");
        Stat(300, cpuTicks: 500);
        _clock.Advance(TimeSpan.FromDays(3));

        var noHistory = await Preview([Agent(), Server()]);
        Record([Agent(), Server()]);
        _clock.Advance(TimeSpan.FromHours(2));
        Stat(300, cpuTicks: 501);
        var usedCpu = await Preview([Agent(), Server()]);

        noHistory.Count.Should().Be(0, "missing history is never idle: the first runs end nothing");
        usedCpu.Count.Should().Be(0, "one tick since the last sighting restarts the idle clock");
    }

    [Fact]
    public async Task An_orphaned_mcp_server_needs_only_the_orphan_idle_minutes_and_a_user_manager_child_is_not_orphaned_enough()
    {
        Stat(300, cpuTicks: 500, parent: 1);
        Stat(310, cpuTicks: 500, parent: 900);
        Stat(320, cpuTicks: 500);
        Stat(200, cpuTicks: 9000, parent: 100, name: "claude");
        var orphan = Server(300, parent: 1, orphaned: true);
        var userManagerChild = Server(310, parent: 900, orphaned: true);
        var underAgent = Server(320);

        var preview = await IdleFor(TimeSpan.FromMinutes(11), [Agent(), orphan, userManagerChild, underAgent]);

        preview.Targets.Select(t => SuspectSignals.Shown(t.Key)).Should().Equal(["300:4000"],
            "a server re-parented to INIT is orphaned after 10 min; one started by the user's systemd manager (the consultation: an 'orphan' by the snapshot's rule, but its client may live) and one under its agent need the full 60");
    }

    [Fact]
    public async Task Another_users_or_roots_server_and_agent_processes_are_never_targets()
    {
        Stat(200, cpuTicks: 9000, parent: 100, name: "claude");
        Stat(300, cpuTicks: 500, uid: 1001);
        Stat(310, cpuTicks: 500, uid: 0);
        var processes = new[] { Agent(), Server(300, user: "other"), Server(310, user: "root") };

        var preview = await IdleFor(TimeSpan.FromHours(2), processes);

        preview.Count.Should().Be(0, "only the target user's servers, never root's, and an agent (idle as it is) is never an MCP server");
    }

    [Fact]
    public async Task A_server_with_a_terminal_or_a_child_or_a_stale_snapshot_is_kept()
    {
        // The risk consultation (2026-10-08): a server waiting on a child (coai-mcp's reviewers run as child CLIs) spends no CPU
        // of its own; a pid whose snapshot start differs from /proc is another process; a terminal is the shared path's guard.
        Stat(200, cpuTicks: 9000, parent: 100, name: "claude");
        Stat(300, cpuTicks: 500, tty: 34816);
        Stat(310, cpuTicks: 500);
        Stat(311, cpuTicks: 100, parent: 310, name: "codex");
        Stat(320, cpuTicks: 500);
        var processes = new[]
        {
            Agent(), Server(300, tty: true), Server(310),
            UserWorld.Process(311, "/usr/bin/codex exec", ageHours: 1) with { ParentPid = 310 },
            Server(320, start: Start + 5),
        };

        var preview = await IdleFor(TimeSpan.FromHours(2), processes);

        preview.Count.Should().Be(0);
        preview.Basis.Should().Contain(McpServerStop.HasTerminal).And.Contain(McpServerStop.HasChild).And.Contain(McpServerStop.NotTheSnapshot);
    }

    [Fact]
    public async Task A_button_run_ends_only_what_its_modal_showed()
    {
        Stat(200, cpuTicks: 9000, parent: 100, name: "claude");
        Stat(300, cpuTicks: 500);
        Stat(310, cpuTicks: 500);
        var processes = new[] { Agent(), Server(300), Server(310) };
        await IdleFor(TimeSpan.FromHours(2), processes);

        var unbound = await Preview(processes, RunTrigger.Manual);
        var bound = await Preview(processes, RunTrigger.Manual, new ShownList(Given: true, new HashSet<string>(["310:4000"], StringComparer.Ordinal)));
        var signals = new RecordingSignals();
        var action = new McpServerStop();
        var context = Context(processes, signals, RunTrigger.Manual, new ShownList(Given: true, new HashSet<string>(["310:4000"], StringComparer.Ordinal)));
        var run = await action.RunAsync(context, bound, new ActionCommands(action, new RecordingCommandRunner(), context.TargetUser, []), CancellationToken.None);

        unbound.Refusal.Should().Be(McpServerStop.ButtonNeedsShownProcesses);
        bound.Targets.Select(t => SuspectSignals.Shown(t.Key)).Should().Equal(["310:4000"]);
        signals.Asked.Should().Equal([new ProcessIdentity(310, Start)], "by pid AND start, and only what the modal showed");
        run.Removed.Should().ContainSingle().Which.Note.Should().StartWith("ended on SIGTERM");
    }

    [Fact]
    public async Task A_server_that_started_a_child_after_the_preview_is_not_signalled()
    {
        // coai code round 2026-10-08, finding 2: the child guard ran at the preview only; a server that started work since then
        // (a child it waits on, spending no CPU of its own) passed every re-check of the signal path.
        Stat(200, cpuTicks: 9000, parent: 100, name: "claude");
        Stat(300, cpuTicks: 500);
        var processes = new[] { Agent(), Server() };
        var preview = await IdleFor(TimeSpan.FromHours(2), processes);
        var signals = new RecordingSignals();
        var action = new McpServerStop();
        var now = Context([.. processes, UserWorld.Process(311, "/usr/bin/codex exec", ageHours: 0.01) with { ParentPid = 300 }], signals);

        var run = await action.RunAsync(now, preview, new ActionCommands(action, new RecordingCommandRunner(), now.TargetUser, []), CancellationToken.None);

        preview.Count.Should().Be(1, "it was idle and childless at the preview");
        signals.Asked.Should().BeEmpty("a child appeared since: it may be waiting on that work");
        run.NotRemoved.Should().ContainSingle().Which.Note.Should().Contain(McpServerStop.HasChild);
    }

    [Fact]
    public async Task An_orphan_window_longer_than_the_idle_window_never_delays_an_orphan()
    {
        // Own code review, finding 6: nothing kept orphanIdleMinutes at or under idleMinutes.
        _sandbox.Write("/etc/wsl-care/config.json", "{ \"mcpWatchdog\": { \"idleMinutes\": 60, \"orphanIdleMinutes\": 120 } }");
        Stat(300, cpuTicks: 500, parent: 1);

        var preview = await IdleFor(TimeSpan.FromMinutes(61), [Server(300, parent: 1, orphaned: true)]);

        preview.Count.Should().Be(1, "an orphan never waits longer than a server whose agent lives");
    }

    [Fact]
    public async Task A_narrowed_button_preview_counts_the_memory_of_what_it_kept()
    {
        // coai code round 2026-10-08, finding 13: the held-memory fact stayed the whole preview's after the narrowing.
        Stat(200, cpuTicks: 9000, parent: 100, name: "claude");
        Stat(300, cpuTicks: 500);
        Stat(310, cpuTicks: 500);
        var processes = new[] { Agent(), Server(300) with { RssAnonBytes = 5_000_000 }, Server(310) with { RssAnonBytes = 7_000_000 } };
        await IdleFor(TimeSpan.FromHours(2), processes);

        var bound = await Preview(processes, RunTrigger.Manual, new ShownList(Given: true, new HashSet<string>(["310:4000"], StringComparer.Ordinal)));

        bound.Facts[SuspectTermination.HeldMemoryFact].Should().Be(7_000_000);
    }
    [Fact]
    public void The_timer_records_mcp_servers_in_the_cpu_history_once_each()
    {
        Stat(200, cpuTicks: 9000, parent: 100, name: "claude");
        Stat(300, cpuTicks: 500);

        Record([Agent(), Server(), Server()]);

        AgentCpuHistory.Read(_sandbox.Paths, _sandbox.Files).Entries.Select(e => e.Pid).Should().Equal([200, 300],
            "the agent and its MCP server, each identity once (the consultation: the merge would refuse a duplicate)");
    }

    [Fact]
    public async Task Auto_A19_is_on_by_default_and_its_trigger_fires_on_any_target()
    {
        Stat(200, cpuTicks: 9000, parent: 100, name: "claude");
        Stat(300, cpuTicks: 500);
        var config = ConfigLoader.Load(_sandbox.Paths, _sandbox.Files).Config;

        var preview = await IdleFor(TimeSpan.FromHours(2), [Agent(), Server()]);
        var action = new McpServerStop();

        config.Bool(ConfigKeys.Auto.A19).Should().BeTrue("the owner's decision of 2026-10-08: automatic, default ON");
        action.Id.ButtonOnly.Should().BeFalse();
        action.Trigger(preview, config).Fired.Should().BeTrue();
        action.Trigger(ActionPreview.Of("x", 0, null, "b", new Dictionary<string, long>(), string.Empty, []), config).Fired.Should().BeFalse();
    }
}
