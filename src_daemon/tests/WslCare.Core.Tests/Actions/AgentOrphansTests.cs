using System.Globalization;
using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Actions.Suspects;
using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;
using WslCare.Core.Json;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions;

/// <summary>
/// A18 (plan §15q E7.S2b, owner decision 2026-10-05): the target user's orphaned AI-agent processes, ended only by the button,
/// only after <c>processes.aiAgentsIdleHours</c> with NO CPU measured by identity across root runs, and only when no session of
/// their (confirmed) agent was written in that window.
/// </summary>
public sealed class AgentOrphansTests : IDisposable
{
    private const string Boot = "6d1c1c5e-0000-4000-8000-000000000001";

    /// <summary>Where a native Claude Code install keeps its program (the catalogue's versions pattern).</summary>
    private const string NativeClaude = "/home/me/.local/share/claude/versions/2.1.0";

    private readonly LinuxSandbox _sandbox = new("a18");
    private readonly ManualTimeProvider _clock = new(FixedTimeProvider.DefaultNow) { SteppedTimestamps = true };
    private readonly Dictionary<string, string> _links = new(StringComparer.Ordinal);
    private readonly LinkedFiles _files;

    public AgentOrphansTests()
    {
        _files = new LinkedFiles(_sandbox.Files, _links);
        BootId(Boot);
    }

    /// <summary>The real sandbox, with the <c>/proc/&lt;pid&gt;/exe</c> links a test names (a link needs a privilege on Windows), and
    /// the folders a test makes unreadable — listed as the physical file system lists one it cannot read: empty unbounded,
    /// <see cref="Core.Files.EntryListing.Unreadable"/> bounded (plan §15q E7.S2d, consultation C-1).</summary>
    private sealed class LinkedFiles(Core.Files.IFileSystem inner, Dictionary<string, string> links) : DelegatingFileSystem(inner), Core.Files.IFileSystem
    {
        public HashSet<string> Unreadable { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override Core.Files.LinkReadResult ReadLink(string path) =>
            links.TryGetValue(path.Replace('\\', '/'), out var target) ? new Core.Files.LinkReadResult.Target(target) : base.ReadLink(path);

        public override IReadOnlyList<Core.Files.FileEntry> ListEntries(string path) =>
            Unreadable.Contains(Path.GetFullPath(path)) ? [] : base.ListEntries(path);

        Core.Files.EntryListing Core.Files.IFileSystem.ListEntries(string path, Core.Files.ListingBounds bounds) =>
            Unreadable.Contains(Path.GetFullPath(path)) ? new Core.Files.EntryListing.Unreadable($"{path}: permission denied") : Inner.ListEntries(path, bounds);
    }

    private void Exe(int pid, string target) => _links[$"{_sandbox.Paths.ProcRoot}/{pid}/exe".Replace('\\', '/')] = target;

    private void Environ(int pid, string variables) => _sandbox.Write($"/proc/{pid}/environ", variables.Replace(";", "\0", StringComparison.Ordinal) + "\0");

    public void Dispose() => _sandbox.Dispose();

    private void BootId(string id) => _sandbox.Write("/proc/sys/kernel/random/boot_id", id + "\n");

    private sealed class RecordingSignals : IProcessSignals
    {
        public List<ProcessIdentity> Asked { get; } = [];

        public Task<IReadOnlyList<SignalOutcome>> TerminateAllAsync(IReadOnlyList<ProcessIdentity> processes, TimeSpan grace, CancellationToken cancellationToken)
        {
            Asked.AddRange(processes);
            return Task.FromResult<IReadOnlyList<SignalOutcome>>([.. processes.Select(_ => new SignalOutcome.Ended(NeededKill: false))]);
        }
    }

    private void Stat(int pid, long cpuTicks, long start = 4000, int tty = 0, int uid = 1000)
    {
        Exe(pid, NativeClaude);
        Environ(pid, "HOME=/home/me;PATH=/usr/bin");
        _sandbox.Write($"/proc/{pid}/stat", string.Create(CultureInfo.InvariantCulture, $"{pid} (claude) S 1 {pid} {pid} {tty} -1 0 0 0 0 0 {cpuTicks} 0 0 0 20 0 1 0 {start} 0 0\n"));
        _sandbox.Write($"/proc/{pid}/status", string.Create(CultureInfo.InvariantCulture, $"Name:\tclaude\nState:\tS (sleeping)\nPPid:\t1\nUid:\t{uid}\t{uid}\t{uid}\t{uid}\nRssAnon:\t2000 kB\nRssShmem:\t0 kB\n"));
    }

    private static ProcessEntry Agent(int pid, string cli = "claude", bool orphaned = true, bool tty = false, string user = "me") =>
        UserWorld.Process(pid, $"/home/me/.local/bin/{cli} --resume", cwd: "/home/me/git/p", family: ProcessFamilies.AiAgents, orphaned: orphaned, tty: tty, ageHours: 48, user: user);

    /// <summary>A terminal's context (<see cref="RunTrigger.Cli"/>: it acts on its own fresh preview), or a button's with the
    /// processes its modal showed.</summary>
    private ActionContext Context(IReadOnlyList<ProcessEntry> processes, IProcessSignals? signals = null, RunTrigger trigger = RunTrigger.Cli, ShownList? shown = null)
    {
        var config = ConfigLoader.Load(_sandbox.Paths, _sandbox.Files).Config;
        return new ActionContext(_sandbox.Paths, _files, _clock, config, trigger, new TargetUserResult.Found(new TargetUser("me", 1000, "/home/me"), "test"))
        {
            Processes = _ => Reading.Of(UserWorld.Snapshot(processes)),
            Signals = signals ?? new RecordingSignals(),
            ShownProcesses = shown ?? ShownList.None,
        };
    }

    private async Task<ActionPreview> Preview(IReadOnlyList<ProcessEntry> processes, RunTrigger trigger = RunTrigger.Cli, ShownList? shown = null)
    {
        var action = new AgentOrphans();
        var context = Context(processes, trigger: trigger, shown: shown);
        return await action.PreviewAsync(context, new ActionCommands(action, new RecordingCommandRunner(), context.TargetUser, []), CancellationToken.None);
    }

    /// <summary>What the timer's full run does every time: record the AI-agent processes' CPU by identity.</summary>
    private void Record(IReadOnlyList<ProcessEntry> processes) =>
        AgentCpuHistory.Record(_sandbox.Paths, _sandbox.Files, processes, SampleTime.Of(_clock), ConfigLoader.Load(_sandbox.Paths, _sandbox.Files).Config).Should().BeEmpty();

    private SampleTime At(TimeSpan later) => new(_clock.GetUtcNow() + later, (long)(TimeSpan.FromDays(1) + later).TotalMilliseconds);

    /// <summary>A Claude Code session written <paramref name="ago"/> before now.</summary>
    private void Session(TimeSpan ago) =>
        _sandbox.Sized("/home/me/.claude/projects/p/s.jsonl", 10, _clock.GetUtcNow() - ago);

    [Fact]
    public async Task Ai_agent_orphan_is_eligible_only_after_N_hours_without_cpu_by_identity()
    {
        Stat(10, cpuTicks: 500);
        Session(TimeSpan.FromDays(3));

        var first = await Preview([Agent(10)]);
        Record([Agent(10)]);
        _clock.Advance(TimeSpan.FromHours(3));
        Record([Agent(10)]);
        var early = await Preview([Agent(10)]);
        _clock.Advance(TimeSpan.FromHours(1.5));
        var late = await Preview([Agent(10)]);
        var written = AgentCpuHistory.Read(_sandbox.Paths, _sandbox.Files);

        first.Count.Should().Be(0, "the first sight starts the clock: missing history is never idle");
        early.Count.Should().Be(0, "3 h without CPU is under the default 4 h");
        late.Targets.Should().ContainSingle().Which.Note.Should().Contain("Claude Code").And.Contain("no CPU for 4.5 h").And.Contain("/home/me/git/p");
        written.Entries.Single().Seen.Should().Be(FixedTimeProvider.DefaultNow.AddHours(3), "a preview writes no state: the last record is the timer's");
    }

    [Fact]
    public async Task Missing_history_never_makes_a_process_eligible()
    {
        Stat(10, cpuTicks: 500);
        _clock.Advance(TimeSpan.FromDays(30));

        (await Preview([Agent(10)])).Count.Should().Be(0);
    }

    [Theory]
    [InlineData("cpu")]
    [InlineData("start")]
    [InlineData("boot")]
    public async Task A_reused_pid_or_a_cpu_tick_or_another_boot_restarts_the_idle_clock(string change)
    {
        Stat(10, cpuTicks: 500);
        Session(TimeSpan.FromDays(3));
        Record([Agent(10)]);
        _clock.Advance(TimeSpan.FromHours(5));
        switch (change)
        {
            case "cpu": Stat(10, cpuTicks: 501); break;
            case "start": Stat(10, cpuTicks: 500, start: 9000); break;
            default: BootId("6d1c1c5e-0000-4000-8000-000000000002"); break;
        }

        (await Preview([Agent(10)])).Count.Should().Be(0, $"a changed {change} is not the idle process the history saw");
        Record([Agent(10)]);
        _clock.Advance(TimeSpan.FromHours(5));
        (await Preview([Agent(10)])).Count.Should().Be(1, "and it becomes idle again only N hours later");
    }

    [Fact]
    public async Task A_live_session_keeps_the_agent_process()
    {
        Stat(10, cpuTicks: 500);
        Record([Agent(10)]);
        _clock.Advance(TimeSpan.FromHours(6));
        Session(TimeSpan.FromHours(1));

        var preview = await Preview([Agent(10)]);

        preview.Count.Should().Be(0);
        preview.Basis.Should().Contain(AgentOrphans.LiveSession);
    }

    [Fact]
    public async Task An_unreadable_sibling_folder_keeps_the_agent_process_cannot_tell()
    {
        // Plan §15q E7.S2d, consultation C-1: an OLD session readable, a sibling project folder unreadable. The listing used to
        // answer an unreadable folder as empty and the scan as complete — so the process became eligible, while a session in
        // the folder nobody could read may have been written a minute ago.
        Stat(10, cpuTicks: 500);
        Session(TimeSpan.FromDays(3));
        Record([Agent(10)]);
        _clock.Advance(TimeSpan.FromHours(6));
        _sandbox.Sized("/home/me/.claude/projects/q/t.jsonl", 10, _clock.GetUtcNow() - TimeSpan.FromMinutes(1));
        _files.Unreadable.Add(Path.GetFullPath(_sandbox.Paths.DistroPath("/home/me/.claude/projects/q")));

        var preview = await Preview([Agent(10)]);

        preview.Count.Should().Be(0, "a folder that could not be listed may hold a live session: cannot tell");
        preview.Basis.Should().Contain(AgentOrphans.CannotTell);
    }

    [Fact]
    public async Task An_unconfirmed_layout_keeps_the_agent_process()
    {
        Stat(10, cpuTicks: 500);
        Record([Agent(10, cli: "copilot")]);
        _clock.Advance(TimeSpan.FromHours(6));

        var preview = await Preview([Agent(10, cli: "copilot")]);

        preview.Count.Should().Be(0);
        preview.Basis.Should().Contain(AgentOrphans.Unconfirmed);
    }

    [Fact]
    public async Task Another_accounts_agent_process_is_never_a_candidate()
    {
        Stat(10, cpuTicks: 500, uid: 1001);
        Session(TimeSpan.FromDays(3));
        Record([Agent(10, user: "other")]);
        _clock.Advance(TimeSpan.FromHours(6));

        var preview = await Preview([Agent(10, user: "other")]);

        preview.Count.Should().Be(0);
        preview.Basis.Should().Contain("0 AI-agent process(es) of me kept", "another account's process is not even judged");
    }

    [Theory]
    [InlineData("tty", AgentOrphans.HasTerminal)]
    [InlineData("parent", AgentOrphans.NotOrphaned)]
    public async Task A_process_with_a_terminal_or_a_live_parent_is_kept(string shape, string why)
    {
        Stat(10, cpuTicks: 500);
        Session(TimeSpan.FromDays(3));
        var process = shape == "tty" ? Agent(10, tty: true) : Agent(10, orphaned: false);
        Record([process]);
        _clock.Advance(TimeSpan.FromHours(6));

        var preview = await Preview([process]);

        preview.Count.Should().Be(0);
        preview.Basis.Should().Contain(why);
    }

    [Fact]
    public async Task The_run_rechecks_identity_and_cpu_before_each_signal()
    {
        Stat(10, cpuTicks: 500);
        Stat(20, cpuTicks: 700, start: 5000);
        Session(TimeSpan.FromDays(3));
        Record([Agent(10), Agent(20)]);
        _clock.Advance(TimeSpan.FromHours(6));
        var signals = new RecordingSignals();
        var action = new AgentOrphans();
        var context = Context([Agent(10), Agent(20)], signals);
        var commands = new ActionCommands(action, new RecordingCommandRunner(), context.TargetUser, []);
        var preview = await action.PreviewAsync(context, commands, CancellationToken.None);
        Stat(20, cpuTicks: 701, start: 5000);

        var run = await action.RunAsync(context, preview, commands, CancellationToken.None);

        preview.Count.Should().Be(2);
        signals.Asked.Should().Equal(new ProcessIdentity(10, 4000));
        run.NotRemoved.Should().ContainSingle().Which.Note.Should().Contain("used CPU");
    }

    [Fact]
    public async Task The_timer_never_ends_an_agent_process()
    {
        _sandbox.Write("/etc/passwd", "root:x:0:0::/root:/bin/bash\nme:x:1000:1000::/home/me:/bin/bash\n");
        _sandbox.Load(0.1, 0.1, 0.1, cpus: 4);
        _sandbox.Write("/home/me/.config/wsl-care/config.json", """{ "dryRun": false, "auto": { "A11": true }, "processes": { "aiAgentsIdleHours": 1 } }""");
        var journal = new List<string>();
        var a18 = new ScriptedAction("A18", journal);
        var context = new EngineContext(_sandbox.Paths, _sandbox.Files, new RecordingCommandRunner { Policy = CommandPolicy.Product }, _clock, new LinuxProbe(_sandbox.Files, _sandbox.Paths, _clock),
            ConfigLoader.Load(_sandbox.Paths, _sandbox.Files), new FakeProcessTable().Alive(4242, FixedTimeProvider.DefaultNow.AddMinutes(-1)), 4242, new ActionRegistry([a18]));
        var engine = new ActionEngine(context);

        var pass = await engine.TimerPassAsync(RunId.New(_clock.GetUtcNow(), 4242), _clock.GetUtcNow(), CancellationToken.None);
        var forced = await engine.ExecuteAsync(new ActRequest([ActionId.Find("A18")!], RunTrigger.Timer, Execute: true), CancellationToken.None);

        journal.Should().BeEmpty("the timer neither selects A18 nor, asked directly, lets it run");
        pass.Ran.Should().BeTrue(pass.Reason);
        pass.Actions.Should().NotContain(o => o.Id == "A18");
        forced.Should().BeOfType<ActResult.Done>().Which.Detail.Actions.Single().Status.Should().Be(ActionStatus.Skipped);
        new AgentOrphans().Trigger(ActionPreview.Unavailable("x", "y"), context.Loaded.Config).Fired.Should().BeFalse();
        ActionId.Find("A18")!.ButtonOnly.Should().BeTrue();
    }

    [Fact]
    public void The_cpu_history_is_root_state_bounded_and_pruned_to_live_processes()
    {
        var now = At(TimeSpan.Zero);
        var before = AgentCpuHistory.Next(AgentCpuFile.Empty, Boot, [new PidSample(10, 1, 5, 0, 1000), new PidSample(11, 1, 5, 0, 1000)], now);
        var many = Enumerable.Range(2, AgentCpuHistory.MaxEntries + 50).Select(pid => new PidSample(pid, pid, 1, 0, 1000)).ToList();

        AgentCpuHistory.Next(before, Boot, [new PidSample(10, 1, 5, 0, 1000)], At(TimeSpan.FromHours(1))).Entries.Select(e => e.Pid).Should().Equal(10);
        var kept = AgentCpuHistory.Next(before, Boot, many, now).Entries;
        kept.Should().HaveCount(AgentCpuHistory.MaxEntries);
        kept.Min(e => e.StartTicks).Should().Be(many.Max(m => m.StartTicks) - AgentCpuHistory.MaxEntries + 1, "review A-L1: past the cap the OLDEST processes are dropped (no history = kept)");
        AgentCpuHistory.Write(_sandbox.Paths, _sandbox.Files, before).Should().BeEmpty();
        AgentCpuHistory.Read(_sandbox.Paths, _sandbox.Files).Should().BeEquivalentTo(before);
        AgentCpuHistory.File(_sandbox.Paths).Should().StartWith(_sandbox.Paths.StateDirectory);
        JsonSerializer.SerializeToUtf8Bytes(AgentCpuHistory.Next(AgentCpuFile.Empty, Boot, [.. many.Take(AgentCpuHistory.MaxEntries)], new SampleTime(DateTimeOffset.MaxValue.AddYears(-1), 999_999_999_999)), WslCareJsonContext.Default.AgentCpuFile).Length
            .Should().BeLessThan(AgentCpuHistory.MaxBytes, "a full history stays inside the read cap");
    }

    [Fact]
    public void Processes_aiAgentsIdleHours_is_1_to_168_default_4_safe_higher()
    {
        var key = ConfigKeys.Processes.AiAgentsIdleHours;

        (key.Min, key.Max).Should().Be((1, 168));
        key.Trust.Safe.Should().Be(SafeDirection.Higher);
        ConfigLoader.Load(_sandbox.Paths, _sandbox.Files).Config.Int(key).Should().Be(4);
        ConfigKeys.Processes.Families.Allowed.Should().NotContain(ProcessFamilies.AiAgents, "Q13: not through the general families list");
    }

    // ---------- E7.S2b review round (security review, plan §15q *E7.S2b/S2c review round*) ----------

    /// <summary>An idle orphan of the default kind, 6 h on the record: Claude Code, native install, its session days old.</summary>
    private async Task<ActionPreview> SixHoursIdle(IReadOnlyList<ProcessEntry> processes, RunTrigger trigger = RunTrigger.Cli, ShownList? shown = null)
    {
        Session(TimeSpan.FromDays(3));
        Record(processes);
        _clock.Advance(TimeSpan.FromHours(3));
        Record(processes);
        _clock.Advance(TimeSpan.FromHours(3));
        return await Preview(processes, trigger, shown);
    }

    [Fact]
    public async Task A_button_run_without_the_processes_its_modal_showed_is_refused()
    {
        Stat(10, cpuTicks: 500);

        var preview = await SixHoursIdle([Agent(10)], RunTrigger.Manual);

        preview.Refusal.Should().Be(AgentOrphans.ButtonNeedsShownProcesses, "review A-H1: a button run is bound to what its modal showed");
    }

    [Fact]
    public async Task A_button_run_ends_only_the_still_eligible_processes_its_modal_showed()
    {
        Stat(10, cpuTicks: 500);
        Stat(20, cpuTicks: 700, start: 5000);
        var shown = ShownList.Of(["10:4000", "30:6000"]);

        var preview = await SixHoursIdle([Agent(10), Agent(20)], RunTrigger.Manual, shown);
        var signals = new RecordingSignals();
        var action = new AgentOrphans();
        var context = Context([Agent(10), Agent(20)], signals, RunTrigger.Manual, shown);
        await action.RunAsync(context, preview, new ActionCommands(action, new RecordingCommandRunner(), context.TargetUser, []), CancellationToken.None);

        preview.Refusal.Should().BeEmpty();
        preview.Targets.Should().ContainSingle().Which.Key.Should().StartWith("10:4000:");
        signals.Asked.Should().Equal([new ProcessIdentity(10, 4000)], "pid 20 became eligible but the modal never showed it; 30 is not eligible now");
    }

    [Fact]
    public async Task The_preview_answers_its_processes_as_pid_and_start_keys()
    {
        Stat(10, cpuTicks: 500);

        var preview = await SixHoursIdle([Agent(10)]);

        new AgentOrphans().Shown(preview).Should().Equal("10:4000");
        (new AgentOrphans() is IBoundToShownList).Should().BeTrue();
    }

    [Fact]
    public async Task A_wrapper_with_a_child_process_is_kept()
    {
        Stat(10, cpuTicks: 500);
        var child = UserWorld.Process(11, "/home/me/.local/share/claude/versions/2.1.0 --worker", family: ProcessFamilies.AiAgents) with { ParentPid = 10 };

        var preview = await SixHoursIdle([Agent(10), child]);

        preview.Count.Should().Be(0, "review A-M1: an idle wrapper's child may be the one working");
        preview.Basis.Should().Contain(AgentOrphans.HasChild);
    }

    [Fact]
    public async Task A_process_the_users_systemd_manager_started_is_not_an_orphan()
    {
        Stat(10, cpuTicks: 500);
        var started = Agent(10) with { ParentPid = 300 };

        var preview = await SixHoursIdle([started]);

        preview.Count.Should().Be(0, "review A-M2: re-parented to systemd --user is a started service, not an orphan");
        preview.Basis.Should().Contain(AgentOrphans.NotOrphaned);
    }

    [Theory]
    [InlineData("/usr/bin/python3", "", false)]
    [InlineData("/home/me/bin/claude", "", false)]
    [InlineData("/usr/bin/node", "/home/me/.npm-global/bin/claude", true)]
    [InlineData("/usr/bin/node", "/home/me/tools/claude", false)]
    public async Task Only_a_program_that_resolves_into_the_agents_own_install_is_that_agent(string exe, string script, bool eligible)
    {
        Stat(10, cpuTicks: 500);
        Exe(10, exe);
        _links[_sandbox.Paths.DistroPath("/home/me/.npm-global/bin/claude").Replace('\\', '/')] = "/home/me/.npm-global/lib/node_modules/@anthropic-ai/claude-code/cli.js";
        var process = script.Length > 0 ? Agent(10) with { CommandLine = $"/usr/bin/node {script} --resume" } : Agent(10);

        var preview = await SixHoursIdle([process]);

        preview.Count.Should().Be(eligible ? 1 : 0, $"review A-M3: {exe} {script}");
        if (!eligible)
        {
            preview.Basis.Should().Contain(AgentOrphans.NotInstalled);
        }
    }

    [Fact]
    public async Task A_wall_clock_jump_after_a_host_sleep_does_not_pass_the_idle_window()
    {
        Stat(10, cpuTicks: 500);
        Session(TimeSpan.FromDays(3));
        Record([Agent(10)]);
        _clock.Advance(TimeSpan.FromHours(1));
        _clock.JumpWallClock(TimeSpan.FromHours(5));
        Record([Agent(10)]);

        var preview = await Preview([Agent(10)]);

        preview.Count.Should().Be(0, "review A-M4: 6 h on the wall clock but 1 h on the monotonic one — the shorter counts");
    }

    [Fact]
    public async Task A_gap_in_the_cpu_history_breaks_the_chain()
    {
        Stat(10, cpuTicks: 500);
        Session(TimeSpan.FromDays(3));
        Record([Agent(10)]);
        _clock.Advance(TimeSpan.FromHours(9));

        (await Preview([Agent(10)])).Count.Should().Be(0, "review A-M4: 9 h since the only sighting is more than two timer periods: no dense chain");

        Record([Agent(10)]);
        _clock.Advance(TimeSpan.FromHours(4));
        Record([Agent(10)]);
        (await Preview([Agent(10)])).Count.Should().Be(0, "the gap stays in the chain while the ticks do not move");
    }

    [Fact]
    public async Task No_session_found_is_cannot_tell_and_a_moved_agent_home_keeps_the_process()
    {
        Stat(10, cpuTicks: 500);
        Directory.CreateDirectory(_sandbox.Paths.DistroPath("/home/me/.claude/projects"));
        Record([Agent(10)]);
        _clock.Advance(TimeSpan.FromHours(3));
        Record([Agent(10)]);
        _clock.Advance(TimeSpan.FromHours(3));

        var none = await Preview([Agent(10)]);
        Session(TimeSpan.FromDays(3));
        Environ(10, "HOME=/home/me;CLAUDE_CONFIG_DIR=/srv/claude-work");
        var moved = await Preview([Agent(10)]);
        Environ(10, "HOME=/home/me;CLAUDE_CONFIG_DIR=/home/me/.claude");
        var defaultHome = await Preview([Agent(10)]);

        none.Count.Should().Be(0, "review A-M5: zero sessions found is not 'no live session'");
        none.Basis.Should().Contain(AgentOrphans.NoSession);
        moved.Count.Should().Be(0);
        moved.Basis.Should().Contain("CLAUDE_CONFIG_DIR");
        defaultHome.Count.Should().Be(1, "the agent's own folder named explicitly is the folder A18 checks");
    }

    [Fact]
    public void The_cpu_history_is_private_to_root()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "file modes are Linux's: run in WSL or on the Linux legs");
        AgentCpuHistory.Write(_sandbox.Paths, _sandbox.Files, AgentCpuHistory.Next(AgentCpuFile.Empty, Boot, [new PidSample(10, 1, 5, 0, 1000)], At(TimeSpan.Zero))).Should().BeEmpty();

        if (OperatingSystem.IsLinux())
        {
            File.GetUnixFileMode(AgentCpuHistory.File(_sandbox.Paths)).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite, "review A-L1: 0600");
        }
    }

    [Fact]
    public async Task A_process_whose_account_changed_since_the_preview_is_not_signalled()
    {
        Stat(10, cpuTicks: 500);
        var preview = await SixHoursIdle([Agent(10)]);
        var signals = new RecordingSignals();
        var action = new AgentOrphans();
        var context = Context([Agent(10)], signals);
        Stat(10, cpuTicks: 500, uid: 1001);

        var run = await action.RunAsync(context, preview, new ActionCommands(action, new RecordingCommandRunner(), context.TargetUser, []), CancellationToken.None);

        preview.Count.Should().Be(1);
        signals.Asked.Should().BeEmpty("review A-L2: the key carries the account, and 1001 is not the 1000 the preview saw");
        run.NotRemoved.Should().ContainSingle().Which.Note.Should().Contain("changed owner");
    }
}
