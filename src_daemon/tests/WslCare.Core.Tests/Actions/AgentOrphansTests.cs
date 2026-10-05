using System.Globalization;
using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Actions.Suspects;
using WslCare.Core.Collectors;
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

    private readonly LinuxSandbox _sandbox = new("a18");
    private readonly ManualTimeProvider _clock = new(FixedTimeProvider.DefaultNow);

    public AgentOrphansTests() => BootId(Boot);

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
        _sandbox.Write($"/proc/{pid}/stat", string.Create(CultureInfo.InvariantCulture, $"{pid} (claude) S 1 {pid} {pid} {tty} -1 0 0 0 0 0 {cpuTicks} 0 0 0 20 0 1 0 {start} 0 0\n"));
        _sandbox.Write($"/proc/{pid}/status", string.Create(CultureInfo.InvariantCulture, $"Name:\tclaude\nState:\tS (sleeping)\nPPid:\t1\nUid:\t{uid}\t{uid}\t{uid}\t{uid}\nRssAnon:\t2000 kB\nRssShmem:\t0 kB\n"));
    }

    private static ProcessEntry Agent(int pid, string cli = "claude", bool orphaned = true, bool tty = false, string user = "me") =>
        UserWorld.Process(pid, $"/home/me/.local/bin/{cli} --resume", cwd: "/home/me/git/p", family: ProcessFamilies.AiAgents, orphaned: orphaned, tty: tty, ageHours: 48, user: user);

    private ActionContext Context(IReadOnlyList<ProcessEntry> processes, IProcessSignals? signals = null)
    {
        var config = ConfigLoader.Load(_sandbox.Paths, _sandbox.Files).Config;
        return new ActionContext(_sandbox.Paths, _sandbox.Files, _clock, config, RunTrigger.Manual, new TargetUserResult.Found(new TargetUser("me", 1000, "/home/me"), "test"))
        {
            Processes = _ => Reading.Of(UserWorld.Snapshot(processes)),
            Signals = signals ?? new RecordingSignals(),
        };
    }

    private async Task<ActionPreview> Preview(IReadOnlyList<ProcessEntry> processes)
    {
        var action = new AgentOrphans();
        var context = Context(processes);
        return await action.PreviewAsync(context, new ActionCommands(action, new RecordingCommandRunner(), context.TargetUser, []), CancellationToken.None);
    }

    /// <summary>What the timer's full run does every time: record the AI-agent processes' CPU by identity.</summary>
    private void Record(IReadOnlyList<ProcessEntry> processes) =>
        AgentCpuHistory.Record(_sandbox.Paths, _sandbox.Files, processes, _clock.GetUtcNow()).Should().BeEmpty();

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
        var now = _clock.GetUtcNow();
        var before = AgentCpuHistory.Next(AgentCpuFile.Empty, Boot, [new SuspectSample(10, 1, 5, 0, 1000), new SuspectSample(11, 1, 5, 0, 1000)], now);
        var many = Enumerable.Range(2, AgentCpuHistory.MaxEntries + 50).Select(pid => new SuspectSample(pid, 1, 1, 0, 1000)).ToList();

        AgentCpuHistory.Next(before, Boot, [new SuspectSample(10, 1, 5, 0, 1000)], now.AddHours(1)).Entries.Select(e => e.Pid).Should().Equal(10);
        AgentCpuHistory.Next(before, Boot, many, now).Entries.Should().HaveCount(AgentCpuHistory.MaxEntries);
        AgentCpuHistory.Write(_sandbox.Paths, _sandbox.Files, before).Should().BeEmpty();
        AgentCpuHistory.Read(_sandbox.Paths, _sandbox.Files).Should().BeEquivalentTo(before);
        AgentCpuHistory.File(_sandbox.Paths).Should().StartWith(_sandbox.Paths.StateDirectory);
        JsonSerializer.SerializeToUtf8Bytes(AgentCpuHistory.Next(AgentCpuFile.Empty, Boot, [.. many.Take(AgentCpuHistory.MaxEntries)], now), WslCareJsonContext.Default.AgentCpuFile).Length
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
}
