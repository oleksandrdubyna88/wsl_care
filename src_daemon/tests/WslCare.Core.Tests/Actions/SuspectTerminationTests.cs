using System.Diagnostics;
using System.Globalization;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Suspects;
using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;
using WslCare.Core.Processes;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions;

/// <summary>
/// A11 (E3.S2): only SUSPECTS — orphaned, in an allowed family, old enough, no terminal, not root's, NO CPU across the
/// window — and each one by pid AND start time; a process that used CPU, gained a terminal or is another process now is
/// never signalled. The signal sender here records what it was asked; the real one (<see cref="PidfdProcessSignals"/>) is
/// tested on Linux against a child THIS test started, by its pid.
/// </summary>
public sealed class SuspectTerminationTests : IDisposable
{
    private readonly LinuxSandbox _sandbox = new("a11");

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

    private void Stat(int pid, long cpuTicks, int tty = 0, long start = 4000, int uid = 1000)
    {
        _sandbox.Write($"/proc/{pid}/stat", string.Create(CultureInfo.InvariantCulture, $"{pid} (VBCSCompiler) S 1 {pid} {pid} {tty} -1 0 0 0 0 0 {cpuTicks} 0 0 0 20 0 1 0 {start} 0 0\n"));
        _sandbox.Write($"/proc/{pid}/status", string.Create(CultureInfo.InvariantCulture, $"Name:\tVBCSCompiler\nState:\tS (sleeping)\nPPid:\t1\nUid:\t{uid}\t{uid}\t{uid}\t{uid}\nRssAnon:\t1000 kB\nRssShmem:\t0 kB\n"));
    }

    private static ProcessEntry Entry(int pid, string family = "dotnet-build-servers", bool orphaned = true, bool tty = false, double hours = 24, string user = "me") =>
        UserWorld.Process(pid, $"dotnet /usr/lib/dotnet/sdk/VBCSCompiler.dll -pipename:{pid}", family: family, orphaned: orphaned, tty: tty, ageHours: hours, user: user);

    private ActionContext Context(IReadOnlyList<ProcessEntry> processes, RecordingSignals signals, Action? duringWindow = null)
    {
        var config = ConfigLoader.Load(_sandbox.Paths, _sandbox.Files).Config;
        return new ActionContext(_sandbox.Paths, _sandbox.Files, new FixedTimeProvider(), config, RunTrigger.Manual, new TargetUserResult.None("machine-scoped"))
        {
            Processes = _ => Reading.Of(UserWorld.Snapshot(processes)),
            Signals = signals,
            Wait = (_, _) =>
            {
                duringWindow?.Invoke();
                return Task.CompletedTask;
            },
        };
    }

    [Fact]
    public void The_candidates_are_orphaned_old_family_members_without_a_terminal_never_root_never_a_zombie()
    {
        var families = new[] { "dotnet-build-servers", "testhost" };
        IReadOnlyList<ProcessEntry> all =
        [
            Entry(10), Entry(11, orphaned: false), Entry(12, tty: true), Entry(13, hours: 2), Entry(14, family: "vscode-server"), Entry(15, user: "root"),
            Entry(16) with { State = 'Z' }, Entry(17, family: "testhost"),
        ];

        SuspectTermination.Candidates(all, families, TimeSpan.FromHours(8), ownPid: 17).Select(p => p.Pid).Should().Equal(10);
    }

    [Fact]
    public async Task A_process_that_used_cpu_in_the_window_is_not_a_suspect_and_one_that_stayed_idle_is_signalled_by_pid_and_start()
    {
        Stat(10, cpuTicks: 100);
        Stat(20, cpuTicks: 100);
        var signals = new RecordingSignals();
        var context = Context([Entry(10), Entry(20)], signals, duringWindow: () => Stat(20, cpuTicks: 101));
        var action = new SuspectTermination();
        var commands = new ActionCommands(action, new RecordingCommandRunner(), context.TargetUser, []);

        var preview = await action.PreviewAsync(context, commands, CancellationToken.None);
        var run = await action.RunAsync(context, preview, commands, CancellationToken.None);

        preview.Targets.Select(t => t.Name).Should().Equal("10 p10");
        signals.Asked.Should().Equal(new ProcessIdentity(10, 4000));
        run.Count.Should().Be(1);
        run.FreedBytes.Should().BeNull("A11 frees memory, not disk");
    }

    [Fact]
    public async Task A11s_dry_run_counts_no_would_free_bytes_because_memory_is_not_disk()
    {
        // Gate finding #4: the memory a suspect holds went into the preview's bytes, so a dry-run week summed it into the
        // logs' "would have freed" — a disk figure. It is a fact and a note now, never bytes.
        Stat(10, cpuTicks: 100);
        var context = Context([Entry(10)], new RecordingSignals());
        var action = new SuspectTermination();
        var preview = await action.PreviewAsync(context, new ActionCommands(action, new RecordingCommandRunner(), context.TargetUser, []), CancellationToken.None);
        var at = new DateTimeOffset(2026, 9, 26, 9, 0, 0, TimeSpan.Zero);
        var record = Core.Actions.Engine.ActionRecords.Of(new Core.Actions.Engine.ActionOutcome("A11", action.Summary, Core.Actions.Engine.ActionStatus.DryRun, "dry run", preview, null));
        new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(new RunRecord(1, RunId.New(at, 5), RunTrigger.Timer, at, at, RunOutcome.Completed, [record]) { DryRun = true });

        var logs = Core.History.RunLogs.Logs(_sandbox.Paths, _sandbox.Files, ((Core.History.PeriodParse.Parsed)Core.History.LogPeriod.Parse("2026-09-26", at)).Period, action: null);

        logs.Runs.WouldFreeBytes.Should().Be(0, "a dry run of A11 would free memory, and the logs' would-free total is disk");
        logs.PerAction.Single().WouldFreeBytes.Should().Be(0);
        preview.Count.Should().Be(1);
        preview.Bytes.Should().BeNull("memory is not disk");
        preview.Facts[SuspectTermination.HeldMemoryFact].Should().Be(1_000_000, "what the suspects hold is kept as a fact");
    }

    [Theory]
    [InlineData("cpu")]
    [InlineData("tty")]
    [InlineData("reused")]
    [InlineData("gone")]
    public async Task Between_preview_and_signal_a_process_that_changed_is_never_signalled(string change)
    {
        Stat(10, cpuTicks: 100);
        var signals = new RecordingSignals();
        var context = Context([Entry(10)], signals);
        var action = new SuspectTermination();
        var commands = new ActionCommands(action, new RecordingCommandRunner(), context.TargetUser, []);
        var preview = await action.PreviewAsync(context, commands, CancellationToken.None);
        switch (change)
        {
            case "cpu": Stat(10, cpuTicks: 150); break;
            case "tty": Stat(10, cpuTicks: 100, tty: 34816); break;
            case "reused": Stat(10, cpuTicks: 100, start: 9000); break;
            default: Directory.Delete(_sandbox.Paths.DistroPath("/proc/10"), recursive: true); break;
        }

        var run = await action.RunAsync(context, preview, commands, CancellationToken.None);

        signals.Asked.Should().BeEmpty();
        run.Count.Should().Be(0);
        run.Succeeded.Should().BeTrue("already gone or not the same process is not a failure");
    }

    [Fact]
    public async Task Without_a_wired_signal_sender_nothing_is_signalled_and_the_action_says_why()
    {
        Stat(10, cpuTicks: 100);
        var context = Context([Entry(10)], new RecordingSignals()) with { Signals = RefusingProcessSignals.Sandboxed };
        var action = new SuspectTermination();
        var commands = new ActionCommands(action, new RecordingCommandRunner(), context.TargetUser, []);

        var run = await action.RunAsync(context, await action.PreviewAsync(context, commands, CancellationToken.None), commands, CancellationToken.None);

        run.Count.Should().Be(0);
        run.Failure.Should().Contain("sandboxed");
    }

    // ---------- the real sender: Linux only, a child this test started ----------

    private static long StartTicks(int pid)
    {
        var path = $"/proc/{pid}/stat";
        return ProcStat.Parse(File.ReadAllText(path), path) is Reading<ProcStat>.Available { Value: var stat } ? stat.StartTicks : -1;
    }

    private static Process Child(string file, params string[] args)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        return Process.Start(start)!;
    }

    [Fact]
    public async Task The_pidfd_sender_ends_its_own_child_on_sigterm_by_pid_and_start()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "pidfd is Linux's: run in WSL or on the Linux legs");
        using var child = Child("sleep", "60");
        try
        {
            var outcome = await new PidfdProcessSignals(new Core.Files.PhysicalFileSystem(Core.Hosting.HostPaths.ForThisMachine(string.Empty)), "/proc")
                .TerminateAsync(new ProcessIdentity(child.Id, StartTicks(child.Id)), TimeSpan.FromSeconds(5), CancellationToken.None);

            outcome.Should().Be(new SignalOutcome.Ended(NeededKill: false));
            child.WaitForExit(5000).Should().BeTrue();
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
            }
        }
    }

    [Fact]
    public async Task The_pidfd_sender_kills_a_child_that_ignores_sigterm_after_the_grace_and_refuses_a_mismatched_start()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "pidfd is Linux's: run in WSL or on the Linux legs");
        using var stubborn = Child("sh", "-c", "trap '' TERM; exec sleep 60");
        using var other = Child("sleep", "60");
        try
        {
            await Task.Delay(300, TestContext.Current.CancellationToken);
            var signals = new PidfdProcessSignals(new Core.Files.PhysicalFileSystem(Core.Hosting.HostPaths.ForThisMachine(string.Empty)), "/proc");

            var mismatched = await signals.TerminateAsync(new ProcessIdentity(other.Id, StartTicks(other.Id) + 1), TimeSpan.FromSeconds(1), CancellationToken.None);
            var killed = await signals.TerminateAsync(new ProcessIdentity(stubborn.Id, StartTicks(stubborn.Id)), TimeSpan.FromSeconds(1), CancellationToken.None);

            mismatched.Should().BeOfType<SignalOutcome.NotTheSame>();
            other.HasExited.Should().BeFalse("a pid whose start does not match is never signalled");
            killed.Should().Be(new SignalOutcome.Ended(NeededKill: true));
        }
        finally
        {
            foreach (var process in new[] { stubborn, other }.Where(p => !p.HasExited))
            {
                process.Kill();
            }
        }
    }

    [Fact]
    public async Task The_pidfd_sender_kills_three_children_that_ignore_sigterm_after_ONE_shared_grace()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "pidfd is Linux's: run in WSL or on the Linux legs");
        var stubborn = Enumerable.Range(0, 3).Select(_ => Child("sh", "-c", "trap '' TERM; exec sleep 60")).ToList();
        try
        {
            await Task.Delay(300, TestContext.Current.CancellationToken);
            var signals = new PidfdProcessSignals(new Core.Files.PhysicalFileSystem(Core.Hosting.HostPaths.ForThisMachine(string.Empty)), "/proc");
            var clock = Stopwatch.StartNew();

            var outcomes = await signals.TerminateAllAsync([.. stubborn.Select(c => new ProcessIdentity(c.Id, StartTicks(c.Id)))], TimeSpan.FromSeconds(2), CancellationToken.None);

            outcomes.Should().AllBeEquivalentTo(new SignalOutcome.Ended(NeededKill: true));
            clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "one 2 s grace across all three, not 2 s each");
        }
        finally
        {
            foreach (var process in stubborn)
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }

                process.Dispose();
            }
        }
    }
}
