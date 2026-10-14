using System.Text.Json;
using System.Text.Json.Nodes;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Archive;
using WslCare.Core.Collectors;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Mcp;
using WslCare.Core.Processes;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// WR-D5 (PLAN_windows_runner.md §3.5, §6): the archive body of <c>tasks run archive</c>, whole program in-process. The supervisor the
/// test hands in records the worker the body chose AND runs that worker's argv through the same program over the same sandbox — so
/// what the body picks is what really happens to the session: a dry window previews and moves nothing, a real one archives, the
/// run lock is never taken, and a second archive is refused by the side's lease naming its holder. The real job is held by
/// <c>Core.Tests/Processes/Win32JobControlTests</c>; the built binary by <c>Scenarios/WindowsTasksRunFlows</c>.
/// </summary>
public sealed class TasksRunArchiveBodyTests : IDisposable
{
    private const string WindowsOnly = "the Windows side's archive over a Windows layout: the Windows legs";
    private const string Self = "C:\\Users\\user\\AppData\\Local\\wsl-care\\bin\\0123\\wsl-care.exe";
    private const string SessionKey = "projects/p/s1.jsonl";
    private const string Transcript = "the transcript";

    private readonly SandboxHost _sandbox = new("tasks-run-archive-body");
    private readonly FixedTimeProvider _clock = new();
    private CliHost _host = null!;

    public void Dispose() => _sandbox.Dispose();

    [Fact]
    public void A_dry_archive_body_runs_the_preview_and_moves_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var session = Session();
        var baseFolder = Configured(dryRunOff: false);
        var supervisor = new RunningSupervisor(() => _host);
        _host = Host(supervisor);

        var (exit, stdout, stderr) = CliRun.Over(_host, "tasks", "run", "archive", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        stdout.Should().BeEmpty("the worker's answer is the supervisor's inherited stdout; the supervisor adds nothing");
        var launch = supervisor.Launches.Should().ContainSingle().Subject;
        launch.Arguments.Should().Equal("archive", "preview", "--json");
        launch.Executable.Should().Be(Self);
        launch.DeadlineKey.Should().Be("timer.runLimitMinutes");
        File.ReadAllText(session).Should().Be(Transcript, "a dry body moves nothing");
        Directory.GetFileSystemEntries(baseFolder).Should().BeEmpty("a preview writes nothing to the base");
        var worker = supervisor.Workers.Should().ContainSingle().Subject;
        JsonSerializer.Deserialize(worker.Stdout, WslCareJsonContext.Default.ArchivePreviewReport)!.Agents.Single(a => a.Id == "claude-code").DueUnits.Should().Be(1);
        Lines(stderr).Should().Contain(l => l.Contains("tasks run archive: a dry run", StringComparison.Ordinal) && l.Contains("archive preview", StringComparison.Ordinal));
        Lines(worker.Stderr).Should().Contain(l => l.Contains("would move 1 session(s), 14 bytes", StringComparison.Ordinal), "the run log records what a real run would move");
        File.Exists(DryRunWindow.File(_sandbox.Paths)).Should().BeTrue("the first task run that reaches the gated body starts the dry-run week");
    }

    [Fact]
    public void A_real_archive_body_runs_archive_run()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var session = Session();
        var baseFolder = Configured(dryRunOff: true);
        StampedDaysAgo(8);
        var supervisor = new RunningSupervisor(() => _host);
        _host = Host(supervisor);

        var (exit, _, stderr) = CliRun.Over(_host, "tasks", "run", "archive", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        supervisor.Launches.Should().ContainSingle().Which.Arguments.Should().Equal("archive", "run", "--json");
        var answer = supervisor.Workers.Should().ContainSingle().Subject.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last();
        var report = JsonSerializer.Deserialize(answer, WslCareJsonContext.Default.ArchiveRunReport)!;
        report.Outcome.Should().Be(RunOutcomes.Done, report.Stop);
        report.Agents.Single(a => a.Id == "claude-code").Copied.Should().Be(1);
        File.ReadAllText(session).Should().Be(Transcript, "phase 1 copies; the source goes only a day later");
        Directory.GetFileSystemEntries(baseFolder).Should().NotBeEmpty("the real run wrote the archive");
        Lines(stderr).Should().NotContain(l => l.Contains("a dry run", StringComparison.Ordinal));
        Lines(stderr).Should().Contain(l => l.Contains("tasks run archive: a real run (dryRun is off and the 7 days since the first timer run", StringComparison.Ordinal), "a real run says why it is real");
    }

    /// <summary>WR-D5 own review M1: a layer in error makes the run observe-only, yet the loader still merges the other entries — so the
    /// machine layer's <c>dryRun</c> false and a week-old stamp would read "real". The body previews, names the observe-only reason, and
    /// never reaches the stamp.</summary>
    [Fact]
    public void An_observe_only_configuration_previews_and_never_runs_archive_run()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        _ = Session();
        var baseFolder = Configured(dryRunOff: true);
        _sandbox.WriteUserConfig(new JsonObject { ["notAKeyOfThisProduct"] = 1, ["archive"] = new JsonObject { ["baseFolder"] = baseFolder } }.ToJsonString());
        StampedDaysAgo(8);
        var stamp = File.ReadAllBytes(DryRunWindow.File(_sandbox.Paths));
        var supervisor = new RunningSupervisor(() => _host);
        _host = Host(supervisor);
        _host.LoadConfig().IsObserveOnly.Should().BeTrue("the fixture must really be observe-only");

        var (_, _, stderr) = CliRun.Over(_host, "tasks", "run", "archive", "--json");

        supervisor.Launches.Should().ContainSingle().Which.Arguments.Should().Equal("archive", "preview", "--json");
        Lines(stderr).Should().Contain(l => l.Contains("tasks run archive: a dry run (observe-only", StringComparison.Ordinal));
        File.ReadAllBytes(DryRunWindow.File(_sandbox.Paths)).Should().Equal(stamp, "the stamp is not touched before the gate");
        Directory.GetFileSystemEntries(baseFolder).Should().BeEmpty();
    }

    /// <summary>WR-D5 own review m1: an elevated supervisor writes nothing — its worker refuses itself, and the week is not started.</summary>
    [Fact]
    public void An_elevated_supervisor_previews_and_does_not_start_the_dry_run_week()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        _ = Session();
        _ = Configured(dryRunOff: true);
        var supervisor = new RunningSupervisor(() => _host);
        _host = Host(supervisor, elevated: true);

        var (_, _, stderr) = CliRun.Over(_host, "tasks", "run", "archive", "--json");

        supervisor.Launches.Should().ContainSingle().Which.Arguments.Should().Equal("archive", "preview", "--json");
        Lines(stderr).Should().Contain(l => l.Contains("an elevated process", StringComparison.Ordinal));
        File.Exists(DryRunWindow.File(_sandbox.Paths)).Should().BeFalse("no stamp: the week is not started by a process that cannot run the archive");
        supervisor.Workers.Should().ContainSingle().Which.Exit.Should().Be((int)ExitCode.NotAsRoot);
    }

    /// <summary>WR-D5 own review M3: a real second archive meets the side's LOCK before the lease; its busy answer names the holder
    /// the held run recorded.</summary>
    [Fact]
    public void A_second_archive_during_a_run_is_refused_busy_naming_the_holder()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        _ = Session();
        _ = Configured(dryRunOff: true);
        StampedDaysAgo(8);
        var supervisor = new RunningSupervisor(() => _host);
        _host = Host(supervisor);
        var state = new ArchiveState(_sandbox.Paths, _sandbox.Files);
        Directory.CreateDirectory(Path.GetDirectoryName(state.LockFile)!);
        _ = state.WriteHolder(new LockHolderRecord(ArchiveState.Version, 4242, 1, "boot-1", _clock.GetUtcNow(), "r-held"));

        int exit;
        using (_sandbox.Files.TryLockExclusive(state.LockFile) is Core.Files.ExclusiveLock.Held held ? held.Handle : throw new InvalidOperationException("the test could not take the side's lock"))
        {
            (exit, _, _) = CliRun.Over(_host, "tasks", "run", "archive", "--json");
        }

        exit.Should().Be((int)ExitCode.Busy, "the worker's own code passes through");
        var answer = supervisor.Workers.Should().ContainSingle().Subject.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last();
        var report = JsonSerializer.Deserialize(answer, WslCareJsonContext.Default.ArchiveRunReport)!;
        report.Outcome.Should().Be(RunOutcomes.Busy);
        report.Stop.Should().Contain("pid 4242").And.Contain("run r-held");
    }

    [Fact]
    public void The_archive_body_never_takes_the_run_lock()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        _ = Session();
        _ = Configured(dryRunOff: true);
        StampedDaysAgo(8);
        var freeWhileRunning = new List<bool>();
        var supervisor = new RunningSupervisor(() => _host) { WhileRunning = () => freeWhileRunning.Add(RunLockIsFree()) };
        _host = Host(supervisor);

        int exit;
        string stderr;
        using (HoldTheRunLock())
        {
            (exit, _, stderr) = CliRun.Over(_host, "tasks", "run", "archive", "--json");
        }

        exit.Should().Be((int)ExitCode.Ok, "a held run lock (a watch sampling) neither stops nor is stopped by the archive: " + stderr);
        supervisor.Workers.Should().ContainSingle().Which.Exit.Should().Be((int)ExitCode.Ok);
        freeWhileRunning.Should().Equal([false], "the test itself still held it when the supervisor launched the worker");

        var free = new List<bool>();
        var alone = new RunningSupervisor(() => _host) { WhileRunning = () => free.Add(RunLockIsFree()) };
        _host = Host(alone);
        CliRun.Over(_host, "tasks", "run", "archive", "--json").Exit.Should().Be((int)ExitCode.Ok);
        free.Should().Equal([true], "at the moment the supervisor launches the worker the run lock is free: the supervisor holds none (what the worker itself takes is the archive's own side lock and lease, asserted by A_second_archive_*)");
    }

    [Fact]
    public void A_second_archive_is_refused_by_the_lease_naming_the_holder()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var session = Session();
        var baseFolder = Configured(dryRunOff: true);
        StampedDaysAgo(8);
        var supervisor = new RunningSupervisor(() => _host);
        _host = Host(supervisor);
        PlantLiveLease(baseFolder, runId: "r-first");

        var (exit, _, stderr) = CliRun.Over(_host, "tasks", "run", "archive", "--json");

        exit.Should().Be((int)ExitCode.RunFailed, "the worker's own code passes through: the second archive was refused; " + stderr);
        var report = JsonSerializer.Deserialize(supervisor.Workers.Should().ContainSingle().Subject.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last(), WslCareJsonContext.Default.ArchiveRunReport)!;
        report.Outcome.Should().Be(RunOutcomes.Refused);
        report.Stop.Should().Contain("another archive run holds the side").And.Contain($"pid {Environment.ProcessId}").And.Contain("run r-first");
        File.ReadAllText(session).Should().Be(Transcript);
        File.Exists(LeaseFile(baseFolder)).Should().BeTrue("the first run's lease is not the second's to remove");
    }

    private static IReadOnlyList<string> Lines(string text) => [.. text.TrimEnd().Split('\n').Select(l => l.TrimEnd('\r'))];

    private string Session()
    {
        var path = Path.Combine(_sandbox.Paths.Home, ".claude", "projects", "p", "s1.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Transcript);
        File.SetLastWriteTimeUtc(path, FixedTimeProvider.DefaultNow.UtcDateTime.AddDays(-40));
        return path;
    }

    /// <summary>The archive base in the sandbox, set in the user layer; <c>dryRun</c> (a key a user layer may only turn ON) in the machine layer.</summary>
    private string Configured(bool dryRunOff)
    {
        var baseFolder = Directory.CreateDirectory(Path.Combine(_sandbox.Root.Path, "archive-base")).FullName;
        _sandbox.WriteUserConfig(new JsonObject { ["archive"] = new JsonObject { ["baseFolder"] = baseFolder } }.ToJsonString());
        if (dryRunOff)
        {
            _sandbox.WriteMachineConfig(new JsonObject { ["dryRun"] = false }.ToJsonString());
        }

        return baseFolder;
    }

    private void StampedDaysAgo(int days)
    {
        Directory.CreateDirectory(_sandbox.Paths.StateDirectory);
        File.WriteAllBytes(DryRunWindow.File(_sandbox.Paths), JsonSerializer.SerializeToUtf8Bytes(new FirstTimerRun(Core.SchemaVersion.Current, _clock.GetUtcNow().AddDays(-days)), WslCareJsonContext.Default.FirstTimerRun));
    }

    private CliHost Host(IWorkerSupervisor supervisor, bool elevated = false) =>
        new(_sandbox.Paths, _sandbox.Files, _clock, new RecordingCommandRunner())
        {
            Probe = new FakeProbe(HostSide.Windows, _clock),
            ProcessPath = () => Self,
            Supervisor = supervisor,
            Privilege = new ProcessPrivilege(elevated, "a test says so"),
            WindowsProcesses = new NoClaude(),
        };

    private bool RunLockIsFree()
    {
        if (_sandbox.Files.TryLockExclusive(_sandbox.Paths.RunLockFile) is not Core.Files.ExclusiveLock.Held held)
        {
            return false;
        }

        held.Handle.Dispose();
        return true;
    }

    private IDisposable HoldTheRunLock() =>
        _sandbox.Files.TryLockExclusive(_sandbox.Paths.RunLockFile) is Core.Files.ExclusiveLock.Held held ? held.Handle : throw new InvalidOperationException("the test could not take the run lock");

    private string LeaseFile(string baseFolder) => Path.Combine(baseFolder, ".wsl-care", "sides", SideName.OfThisProcess(_sandbox.Paths.Side) + ".lease");

    /// <summary>A lease that THIS test process holds, as the product's own run would write it (this host, this boot, this pid, this start).</summary>
    private void PlantLiveLease(string baseFolder, string runId)
    {
        var boot = _host.Processes.Boot();
        var alive = _host.Processes.Lookup(Environment.ProcessId).Should().BeOfType<ProcessLookup.Alive>().Subject;
        var holder = new LeaseRecord(1, Environment.MachineName, boot.BootId, Environment.ProcessId, alive.StartTicks ?? 0, alive.StartUtc, runId, _clock.GetUtcNow());
        Directory.CreateDirectory(Path.GetDirectoryName(LeaseFile(baseFolder))!);
        File.WriteAllBytes(LeaseFile(baseFolder), JsonSerializer.SerializeToUtf8Bytes(holder, WslCareJsonContext.Compact.LeaseRecord));
    }

    /// <summary>Records the launch, then runs its argv through the same program over the same host — what the real worker would do.</summary>
    private sealed class RunningSupervisor(Func<CliHost> host) : IWorkerSupervisor
    {
        public List<WorkerLaunch> Launches { get; } = [];

        public List<(int Exit, string Stdout, string Stderr)> Workers { get; } = [];

        public Action WhileRunning { get; init; } = static () => { };

        public WorkerOutcome Run(WorkerLaunch launch, CancellationToken cancellationToken)
        {
            Launches.Add(launch);
            WhileRunning();
            var worker = CliRun.Over(host(), Serilog.Core.Logger.None, cancellationToken, [.. launch.Arguments]);
            Workers.Add(worker);
            return new WorkerOutcome.Exited(worker.Exit);
        }
    }

    private sealed class NoClaude : IWindowsProcessTable
    {
        public Reading<IReadOnlyList<WindowsProcessEntry>> List() => Reading.Of<IReadOnlyList<WindowsProcessEntry>>([new WindowsProcessEntry(10, 4, "explorer.exe")]);

        public WindowsProcessDetails Details(int pid) => WindowsProcessDetails.Unopenable("not asked");
    }
}
