using System.Globalization;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Actions.Suspects;
using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Processes;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions;

/// <summary>
/// A21 (plan E14 S7b.2, from the Q-S7b-3 measurement of 2026-10-10): the target user's interop relays of catalogued Windows MCP
/// servers whose client is gone — re-parented to a reaper, not born there, stdio pipes nobody else holds, idle — end on SIGTERM
/// ONLY, by pid and start. The /proc tree is a synthetic fixture (the links a test names, since a link needs a privilege on
/// Windows), and the signal sender only records: no test ever signals a real process.
/// </summary>
public sealed class InteropRelayStopTests : IDisposable
{
    private const string Boot = "6d1c1c5e-0000-4000-8000-000000000021";
    private const string CredsExe = "/mnt/c/Users/me/AppData/Local/Programs/creds/creds-mcp.exe";
    private const long Start = 36501;
    private const int Relay = 12062;
    private const int SessionInit = 7410;

    private readonly LinuxSandbox _sandbox = new("a21");
    private readonly ManualTimeProvider _clock = new(FixedTimeProvider.DefaultNow) { SteppedTimestamps = true };
    private readonly Dictionary<string, string> _links = new(StringComparer.Ordinal);
    private readonly LinkedFiles _files;

    public InteropRelayStopTests()
    {
        _files = new LinkedFiles(_sandbox.Files, _links);
        _sandbox.Write("/proc/sys/kernel/random/boot_id", Boot + "\n");
    }

    public void Dispose() => _sandbox.Dispose();

    /// <summary>The real sandbox with the links a test names, and the folders and links a test makes unreadable.</summary>
    private sealed class LinkedFiles(IFileSystem inner, Dictionary<string, string> links) : DelegatingFileSystem(inner), IFileSystem
    {
        public HashSet<string> UnreadableDirectories { get; } = new(StringComparer.Ordinal);

        public HashSet<string> UnreadableLinks { get; } = new(StringComparer.Ordinal);

        public override LinkReadResult ReadLink(string path)
        {
            var key = path.Replace('\\', '/');
            return UnreadableLinks.Contains(key) ? new LinkReadResult.Unreadable("permission denied")
                : links.TryGetValue(key, out var target) ? new LinkReadResult.Target(target)
                : new LinkReadResult.NotALink();
        }

        EntryListing IFileSystem.ListEntries(string path, ListingBounds bounds) =>
            UnreadableDirectories.Contains(path.Replace('\\', '/')) ? new EntryListing.Unreadable($"{path}: permission denied") : Inner.ListEntries(path, bounds);
    }

    private sealed class RecordingSignals : IProcessSignals
    {
        public List<ProcessIdentity> Escalated { get; } = [];

        public List<ProcessIdentity> TermOnly { get; } = [];

        public Func<ProcessIdentity, SignalOutcome> Outcome { get; init; } = static _ => new SignalOutcome.Ended(NeededKill: false);

        public Task<IReadOnlyList<SignalOutcome>> TerminateAllAsync(IReadOnlyList<ProcessIdentity> processes, TimeSpan grace, CancellationToken cancellationToken)
        {
            Escalated.AddRange(processes);
            return Task.FromResult<IReadOnlyList<SignalOutcome>>([.. processes.Select(_ => new SignalOutcome.Ended(NeededKill: true))]);
        }

        public Task<IReadOnlyList<SignalOutcome>> TerminateOnlyAsync(IReadOnlyList<ProcessIdentity> processes, TimeSpan grace, CancellationToken cancellationToken)
        {
            TermOnly.AddRange(processes);
            return Task.FromResult<IReadOnlyList<SignalOutcome>>([.. processes.Select(Outcome)]);
        }
    }

    private string Proc(int pid) => $"{_sandbox.Paths.ProcRoot}/{pid.ToString(CultureInfo.InvariantCulture)}".Replace('\\', '/');

    private void Link(int pid, string name, string target)
    {
        _sandbox.Write($"/proc/{pid}/{name}", string.Empty);
        _links[$"{Proc(pid)}/{name}"] = target;
    }

    /// <summary>One process's <c>stat</c>, <c>status</c> and <c>comm</c> as /proc answers them.</summary>
    private void Stat(int pid, long cpuTicks = 500, int parent = SessionInit, long start = Start, int tty = 0, int uid = 1000, string name = "init")
    {
        _sandbox.Write($"/proc/{pid}/stat", string.Create(CultureInfo.InvariantCulture, $"{pid} ({name}) S {parent} {pid} {pid} {tty} -1 0 0 0 0 0 {cpuTicks} 0 0 0 20 0 1 0 {start} 0 0\n"));
        _sandbox.Write($"/proc/{pid}/status", string.Create(CultureInfo.InvariantCulture, $"Name:\t{name}\nState:\tS (sleeping)\nPPid:\t{parent}\nUid:\t{uid}\t{uid}\t{uid}\t{uid}\nRssAnon:\t30000 kB\nRssShmem:\t0 kB\n"));
        _sandbox.Write($"/proc/{pid}/comm", name + "\n");
    }

    /// <summary>The WSL session init <c>Relay(n)</c> — root's.</summary>
    private void Init(int pid = SessionInit, int child = 7411, int uid = 0, string comm = "") =>
        Stat(pid, cpuTicks: 10, parent: 1, uid: uid, name: comm.Length > 0 ? comm : $"Relay({child})");

    /// <summary>A relay on /proc: exe <c>/init</c>, argv <c>/init &lt;exe&gt;</c>, stdio pipes 100+pid*3.. unless given.</summary>
    private void RelayOnProc(int pid = Relay, int parent = SessionInit, string exe = CredsExe, string exeLink = "/init", string[]? stdio = null, int uid = 1000, long cpu = 500)
    {
        Stat(pid, cpuTicks: cpu, parent: parent, uid: uid);
        _sandbox.Write($"/proc/{pid}/cmdline", $"/init\0{exe}\0");
        _links[$"{Proc(pid)}/exe"] = exeLink;
        var links = stdio ?? [Pipe(pid, 0), Pipe(pid, 1), Pipe(pid, 2)];
        for (var fd = 0; fd < links.Length; fd++)
        {
            if (links[fd].Length > 0)
            {
                Link(pid, $"fd/{fd}", links[fd]);
            }
        }
    }

    private static string Pipe(int pid, int fd) => string.Create(CultureInfo.InvariantCulture, $"pipe:[{(pid * 10) + fd}]");

    private static ProcessEntry Entry(int pid = Relay, int parent = SessionInit, string exe = CredsExe, string user = "me", bool tty = false, long start = Start) =>
        UserWorld.Process(pid, $"/init {exe}", user: user, tty: tty, ageHours: 6) with { ParentPid = parent, StartTicks = Reading.Of(start) };

    private ActionContext Context(IReadOnlyList<ProcessEntry> processes, IProcessSignals? signals = null, RunTrigger trigger = RunTrigger.Cli, ShownList? shown = null)
    {
        var config = ConfigLoader.Load(_sandbox.Paths, _files).Config;
        return new ActionContext(_sandbox.Paths, _files, _clock, config, trigger, new TargetUserResult.Found(new TargetUser("me", 1000, "/home/me"), "test"))
        {
            Processes = _ => Reading.Of(UserWorld.Snapshot(processes)),
            Signals = signals ?? new RecordingSignals(),
            ShownProcesses = shown ?? ShownList.None,
        };
    }

    private async Task<ActionPreview> Preview(IReadOnlyList<ProcessEntry> processes, RunTrigger trigger = RunTrigger.Cli, ShownList? shown = null)
    {
        var action = new InteropRelayStop();
        var context = Context(processes, trigger: trigger, shown: shown);
        return await action.PreviewAsync(context, new ActionCommands(action, new RecordingCommandRunner(), context.TargetUser, []), CancellationToken.None);
    }

    /// <summary>What the timer and the watch do: record the CPU history as the configuration has it — relays included.</summary>
    private void Record(IReadOnlyList<ProcessEntry> processes) =>
        AgentCpuHistory.Record(_sandbox.Paths, _files, processes, SampleTime.Of(_clock), ConfigLoader.Load(_sandbox.Paths, _files).Config).Should().BeEmpty();

    private async Task<ActionPreview> IdleFor(TimeSpan apart, IReadOnlyList<ProcessEntry> processes)
    {
        Record(processes);
        _clock.Advance(apart);
        Record(processes);
        return await Preview(processes);
    }

    private static readonly TimeSpan PastTheWindow = TimeSpan.FromMinutes(11);

    [Fact]
    public async Task A_client_gone_idle_relay_of_a_catalogued_program_is_a_target_by_pid_and_start()
    {
        Init();
        RelayOnProc();

        var preview = await IdleFor(PastTheWindow, [Entry()]);

        var target = preview.Targets.Should().ContainSingle().Subject;
        target.Key.Should().Be("12062:36501:500:1000", "the identity the signal path re-checks: pid, start ticks, CPU ticks, uid");
        target.Name.Should().Be("12062 creds-mcp");
        target.Note.Should().Contain("Relay(7411)").And.Contain("client is gone").And.Contain("creds-mcp.exe").And.NotContain("/mnt/c/Users", "the Windows path carries the Windows user name");
    }

    [Fact]
    public async Task A_relay_with_a_live_caller_is_kept()
    {
        Stat(13000, cpuTicks: 9000, parent: 1, name: "creds-mcp");
        RelayOnProc(parent: 13000);

        var preview = await IdleFor(PastTheWindow, [Entry(parent: 13000)]);

        preview.Count.Should().Be(0);
        preview.Basis.Should().Contain(InteropRelayStop.CallerMayLive);
    }

    [Fact]
    public async Task A_relay_whose_pid_is_its_Relay_parents_n_is_kept_as_born_there()
    {
        Init(child: Relay);
        RelayOnProc();

        var preview = await IdleFor(PastTheWindow, [Entry()]);

        preview.Count.Should().Be(0, "Relay(12062) was made for pid 12062: wsl.exe's own command, whose Windows caller may live");
        preview.Basis.Should().Contain(InteropRelayStop.BornThere);
    }

    [Theory]
    [InlineData(1, "systemd")]
    [InlineData(SessionInit, "SessionLeader")]
    public async Task A_relay_orphaned_to_pid_1_or_SessionLeader_is_judged_like_one_under_Relay(int parent, string comm)
    {
        if (parent == 1)
        {
            Stat(1, cpuTicks: 10, parent: 0, uid: 0, name: comm);
        }
        else
        {
            Init(comm: comm);
        }

        RelayOnProc(parent: parent);

        var preview = await IdleFor(PastTheWindow, [Entry(parent: parent)]);

        preview.Targets.Should().ContainSingle().Which.Key.Should().StartWith("12062:36501:");
    }

    [Fact]
    public async Task A_non_root_parent_named_Relay_is_not_a_reaper()
    {
        Init(uid: 1000);
        RelayOnProc();

        var preview = await IdleFor(PastTheWindow, [Entry()]);

        preview.Count.Should().Be(0, "any process can name itself Relay(n); only root's is the session init");
        preview.Basis.Should().Contain(InteropRelayStop.CallerMayLive);
    }

    [Fact]
    public async Task A_relay_whose_stdio_another_process_holds_is_kept_including_a_root_holder_and_the_session_init()
    {
        Init();
        RelayOnProc();
        Stat(SessionInit + 1, cpuTicks: 10, parent: 1, uid: 0, name: "other-root");
        Link(SessionInit + 1, "fd/7", Pipe(Relay, 1));

        var heldByRoot = await IdleFor(PastTheWindow, [Entry()]);
        _links.Remove($"{Proc(SessionInit + 1)}/fd/7");
        Link(SessionInit, "fd/4", Pipe(Relay, 0));
        var heldByTheInit = await Preview([Entry()]);

        heldByRoot.Count.Should().Be(0);
        heldByRoot.Basis.Should().Contain(InteropRelayStop.StdioHeld).And.Contain(string.Create(CultureInfo.InvariantCulture, $"pid {SessionInit + 1}"));
        heldByTheInit.Count.Should().Be(0, "the session init holding the pipe counts as a holder too");
        heldByTheInit.Basis.Should().Contain(string.Create(CultureInfo.InvariantCulture, $"pid {SessionInit}"));
    }

    /// <summary>The coai plan round 2026-10-10, finding 0: a live caller may keep only stderr.</summary>
    [Fact]
    public async Task A_relay_whose_stderr_pipe_another_process_holds_is_kept()
    {
        Init();
        RelayOnProc();
        Stat(500, cpuTicks: 10, parent: 1, name: "caller");
        Link(500, "fd/2", Pipe(Relay, 2));

        var preview = await IdleFor(PastTheWindow, [Entry()]);

        preview.Count.Should().Be(0);
        preview.Basis.Should().Contain(InteropRelayStop.StdioHeld);
    }

    [Theory]
    [InlineData("socket:[900]", "pipe:[901]", "/dev/null", InteropRelayStop.StdioSocket)]
    [InlineData("pipe:[900]", "pipe:[901]", "socket:[902]", InteropRelayStop.StdioSocket)]
    [InlineData("/dev/null", "/home/me/out.log", "/home/me/out.log", InteropRelayStop.StdioNotPiped)]
    [InlineData("pipe:[900]", "", "/dev/null", InteropRelayStop.StdioNotPiped)]
    public async Task A_relay_whose_stdio_is_a_socket_a_file_or_closed_is_kept(string fd0, string fd1, string fd2, string why)
    {
        Init();
        RelayOnProc(stdio: [fd0, fd1, fd2]);

        var preview = await IdleFor(PastTheWindow, [Entry()]);

        preview.Count.Should().Be(0);
        preview.Basis.Should().Contain(why);
    }

    [Fact]
    public async Task A_relay_with_piped_stdin_and_stdout_and_stderr_to_a_file_is_a_target()
    {
        Init();
        RelayOnProc(stdio: [Pipe(Relay, 0), Pipe(Relay, 1), "/dev/null"]);

        var preview = await IdleFor(PastTheWindow, [Entry()]);

        preview.Count.Should().Be(1);
    }

    [Fact]
    public async Task An_unreadable_fd_table_or_link_keeps_every_relay()
    {
        Init();
        RelayOnProc();
        RelayOnProc(pid: Relay + 1, exe: CredsExe);
        Stat(600, cpuTicks: 10, parent: 1, name: "hardened");
        _sandbox.Write("/proc/600/fd/0", string.Empty);
        _files.UnreadableDirectories.Add($"{Proc(600)}/fd");

        var tableUnreadable = await IdleFor(PastTheWindow, [Entry(), Entry(pid: Relay + 1)]);
        _files.UnreadableDirectories.Clear();
        _files.UnreadableLinks.Add($"{Proc(600)}/fd/0");
        var linkUnreadable = await Preview([Entry(), Entry(pid: Relay + 1)]);

        tableUnreadable.Count.Should().Be(0, "a holder in a table the scan cannot read cannot be ruled out");
        tableUnreadable.Basis.Should().Contain("inconclusive").And.Contain("pid 600");
        linkUnreadable.Count.Should().Be(0);
        linkUnreadable.Basis.Should().Contain("pid 600's fd 0");
    }

    [Fact]
    public async Task A_vanished_process_during_the_scan_keeps_nothing()
    {
        Init();
        RelayOnProc();
        // A pid the listing names whose directory is gone by the time its table is read: normal churn, it holds nothing.
        _sandbox.Write("/proc/700/fd/.keep", string.Empty);
        _files.UnreadableDirectories.Add($"{Proc(700)}/fd");
        var gone = new GoneFiles(_files, $"{Proc(700)}");

        var preview = await IdleForWith(gone, PastTheWindow, [Entry()]);

        preview.Count.Should().Be(1, "a process that ended mid-scan holds no pipe any more");
    }

    /// <summary>A file system where one process's directory reads as gone (its table unreadable, the directory absent).</summary>
    private sealed class GoneFiles(IFileSystem inner, string goneDirectory) : DelegatingFileSystem(inner), IFileSystem
    {
        public override bool DirectoryExists(string path) => path.Replace('\\', '/') != goneDirectory && base.DirectoryExists(path);

        EntryListing IFileSystem.ListEntries(string path, ListingBounds bounds) => Inner.ListEntries(path, bounds);
    }

    private async Task<ActionPreview> IdleForWith(IFileSystem files, TimeSpan apart, IReadOnlyList<ProcessEntry> processes)
    {
        var config = ConfigLoader.Load(_sandbox.Paths, files).Config;
        AgentCpuHistory.Record(_sandbox.Paths, files, processes, SampleTime.Of(_clock), config).Should().BeEmpty();
        _clock.Advance(apart);
        AgentCpuHistory.Record(_sandbox.Paths, files, processes, SampleTime.Of(_clock), config).Should().BeEmpty();
        var action = new InteropRelayStop();
        var context = new ActionContext(_sandbox.Paths, files, _clock, config, RunTrigger.Cli, new TargetUserResult.Found(new TargetUser("me", 1000, "/home/me"), "test"))
        {
            Processes = _ => Reading.Of(UserWorld.Snapshot(processes)),
            Signals = new RecordingSignals(),
        };
        return await action.PreviewAsync(context, new ActionCommands(action, new RecordingCommandRunner(), context.TargetUser, []), CancellationToken.None);
    }

    [Fact]
    public async Task A_relay_that_used_cpu_within_the_orphan_window_or_has_no_history_is_kept()
    {
        Init();
        RelayOnProc();

        var noHistory = await Preview([Entry()]);
        var early = await IdleFor(TimeSpan.FromMinutes(5), [Entry()]);

        noHistory.Count.Should().Be(0, "no history is never idle");
        early.Count.Should().Be(0, "5 minutes is under mcpWatchdog.orphanIdleMinutes (10)");
        early.Basis.Should().Contain(InteropRelayStop.UsedCpu);
    }

    [Theory]
    [InlineData("/mnt/c/Tools/other-tool.exe", "/init")]
    [InlineData(CredsExe, "/usr/bin/python3")]
    [InlineData("/mnt/c/Tools/creds-mcp", "/init")]
    public async Task A_relay_of_an_uncatalogued_program_or_a_non_init_exe_is_never_a_target(string exe, string exeLink)
    {
        Init();
        RelayOnProc(exe: exe, exeLink: exeLink);

        var preview = await IdleFor(PastTheWindow, [Entry(exe: exe)]);

        preview.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_relay_of_a_user_program_is_never_a_target_in_this_story()
    {
        _sandbox.Write("/etc/wsl-care/config.json", """{ "mcpServers": { "programs": ["my-tool"] } }""");
        Init();
        RelayOnProc(exe: "/mnt/c/Tools/my-tool.exe");

        var preview = await IdleFor(PastTheWindow, [Entry(exe: "/mnt/c/Tools/my-tool.exe")]);

        preview.Count.Should().Be(0, "a name the user chose may be another program, and every client-gone relay is an orphan");
    }

    [Fact]
    public async Task A_relay_program_matches_without_case_and_without_a_mnt_prefix()
    {
        const string Moved = "/windows/c/Users/me/AppData/Local/Programs/creds/CREDS-MCP.EXE";
        Init();
        RelayOnProc(exe: Moved);

        var preview = await IdleFor(PastTheWindow, [Entry(exe: Moved)]);

        preview.Count.Should().Be(1, "[automount] root= moves the prefix, and a Windows file name has no case");
    }

    [Fact]
    public async Task Another_users_or_roots_relay_or_one_with_a_terminal_is_never_a_target()
    {
        Init();
        RelayOnProc(uid: 1001);
        RelayOnProc(pid: Relay + 1, uid: 0);
        RelayOnProc(pid: Relay + 2);

        var preview = await IdleFor(PastTheWindow, [Entry(user: "other"), Entry(pid: Relay + 1, user: "root"), Entry(pid: Relay + 2, tty: true)]);

        preview.Count.Should().Be(0);
        preview.Basis.Should().Contain("1 relay(s) of me kept").And.Contain(InteropRelayStop.HasTerminal);
    }

    [Fact]
    public async Task The_stop_is_SIGTERM_only_and_a_survivor_is_reported_still_running_naming_its_Windows_program()
    {
        Init();
        RelayOnProc();
        var processes = new[] { Entry() };
        var preview = await IdleFor(PastTheWindow, processes);
        var signals = new RecordingSignals { Outcome = static p => new SignalOutcome.StillRunning($"pid {p.Pid} did not end within 10 s of SIGTERM; no SIGKILL is sent") };
        var action = new InteropRelayStop();
        var context = Context(processes, signals);

        var run = await action.RunAsync(context, preview, new ActionCommands(action, new RecordingCommandRunner(), context.TargetUser, []), CancellationToken.None);

        signals.TermOnly.Should().Equal([new ProcessIdentity(Relay, Start)]);
        signals.Escalated.Should().BeEmpty("no SIGKILL is ever sent to a relay");
        run.Count.Should().Be(0);
        run.NotRemoved.Should().ContainSingle().Which.Note.Should().Contain("no SIGKILL").And.Contain("creds-mcp.exe is still running");
        run.Failure.Should().Contain("creds-mcp.exe is still running");
    }

    [Fact]
    public async Task A_client_that_returned_or_a_reused_pid_before_the_signal_stops_nothing()
    {
        Init();
        RelayOnProc();
        RelayOnProc(pid: Relay + 1);
        var processes = new[] { Entry(), Entry(pid: Relay + 1) };
        var preview = await IdleFor(PastTheWindow, processes);
        preview.Count.Should().Be(2);
        Stat(800, cpuTicks: 10, parent: 1, name: "caller");
        Link(800, "fd/5", Pipe(Relay, 1));
        Stat(Relay + 1, cpuTicks: 500, start: Start + 99);
        var signals = new RecordingSignals();
        var action = new InteropRelayStop();
        var context = Context([Entry(), Entry(pid: Relay + 1, start: Start + 99)], signals);

        var run = await action.RunAsync(context, preview, new ActionCommands(action, new RecordingCommandRunner(), context.TargetUser, []), CancellationToken.None);

        signals.TermOnly.Should().BeEmpty("one relay's pipe has a holder again, the other pid is another process now");
        run.NotRemoved.Should().HaveCount(2).And.OnlyContain(i => i.Note.StartsWith("not signalled: the re-check", StringComparison.Ordinal));
        run.NotRemoved.Should().Contain(i => i.Note.Contains(InteropRelayStop.StdioHeld, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_button_run_ends_only_what_its_modal_showed_and_one_without_a_shown_list_is_refused()
    {
        Init();
        RelayOnProc();
        RelayOnProc(pid: Relay + 1);
        var processes = new[] { Entry(), Entry(pid: Relay + 1) };
        Record(processes);
        _clock.Advance(PastTheWindow);
        Record(processes);

        var bare = await Preview(processes, RunTrigger.Manual);
        var bound = await Preview(processes, RunTrigger.Manual, ShownList.Of([$"{Relay + 1}:{Start}"]));

        bare.Refusal.Should().Be(InteropRelayStop.ButtonNeedsShownProcesses);
        bound.Targets.Should().ContainSingle().Which.Key.Should().StartWith($"{Relay + 1}:{Start}:");
    }

    [Fact]
    public void The_timer_and_the_watch_record_relays_in_the_cpu_history()
    {
        Init();
        RelayOnProc();

        Record([Entry()]);

        var history = AgentCpuHistory.Read(_sandbox.Paths, _files);
        history.Entries.Should().ContainSingle(e => e.Pid == Relay && e.StartTicks == Start, "a relay's argv[0] is init, so only the relay test records it");
    }

    /// <summary>The status block (plan E14 S7b.2 item 9): what an unprivileged status can read — the relays, the re-parented and not
    /// born there — and the EFFECTIVE auto.A21 with the command that flips it (the extension never writes daemon config).</summary>
    [Fact]
    public void The_status_block_counts_relays_and_the_reparented_and_names_the_switch_and_its_command()
    {
        Init();
        RelayOnProc();
        Init(pid: SessionInit + 2, child: Relay + 1);
        RelayOnProc(pid: Relay + 1, parent: SessionInit + 2);
        Stat(13000, cpuTicks: 9000, parent: 1, name: "creds-mcp");
        RelayOnProc(pid: Relay + 2, parent: 13000);
        var processes = new[] { Entry(), Entry(pid: Relay + 1, parent: SessionInit + 2), Entry(pid: Relay + 2, parent: 13000) };

        var on = Core.Status.InteropRelaysReport.From(Reading.Of(UserWorld.Snapshot(processes)), _sandbox.Paths, _files, ConfigLoader.Load(_sandbox.Paths, _files).Config);
        _sandbox.Write("/etc/wsl-care/config.json", """{ "auto": { "A21": false } }""");
        var off = Core.Status.InteropRelaysReport.From(Reading.Of(UserWorld.Snapshot(processes)), _sandbox.Paths, _files, ConfigLoader.Load(_sandbox.Paths, _files).Config);

        (on.Available, on.Count, on.Reparented).Should().Be((true, 3, 1), "three relays; one under a live caller, one born under its Relay(n)");
        (on.AutoStop, on.Command).Should().Be((true, "wsl-care config set auto.A21 false"));
        (off.AutoStop, off.Command).Should().Be((false, "wsl-care config set auto.A21 true"));
    }
}
