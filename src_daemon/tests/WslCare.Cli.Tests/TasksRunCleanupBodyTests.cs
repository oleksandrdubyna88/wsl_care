using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Processes;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// WR-T2 (PLAN_windows_runner.md §3.12 "On the runner", §6): the cleanup body of <c>tasks run cleanup</c>, whole program in-process. The
/// supervisor the test hands in records the worker (<c>act A23 --timer --confirm --json</c>) AND runs that argv through the same program
/// over the same sandbox, so what the table spells is what really happens to the sandbox's <c>%TEMP%</c>. No test resolves the real
/// <c>%TEMP%</c>, <c>%LOCALAPPDATA%</c> or home: every path is under a <see cref="TempRoot"/>.
/// </summary>
public sealed class TasksRunCleanupBodyTests : IDisposable
{
    private const string WindowsOnly = "the Windows side's cleanup over a Windows layout and its owners: the Windows legs";
    private const string Self = "C:\\Users\\user\\AppData\\Local\\wsl-care\\bin\\0123\\wsl-care.exe";

    private readonly SandboxHost _sandbox = new("tasks-run-cleanup-body");
    private readonly TimeProvider _clock = TimeProvider.System;
    private CliHost _host = null!;

    public void Dispose() => _sandbox.Dispose();

    private string Temp => _sandbox.Paths.TempDirectory;

    private string Cursor => Path.Combine(_sandbox.Paths.StateDirectory, "temp-cursor.json");

    private string Make(string relative, int daysOld, params (string File, int Days)[] files) => TempTrees.Make(Temp, relative, daysOld, files);

    private CliHost Host(IWorkerSupervisor supervisor, bool elevated = false)
    {
        Directory.CreateDirectory(Temp);
        return new CliHost(_sandbox.Paths, _sandbox.Files, _clock, new RecordingCommandRunner())
        {
            Probe = new FakeProbe(HostSide.Windows, _clock),
            ProcessPath = () => Self,
            Supervisor = supervisor,
            Privilege = new ProcessPrivilege(elevated, "a test says so"),
            UserSid = () => _sandbox.Files.OwnerSidOf(Temp),
        };
    }

    private void StampA23DaysAgo(int days)
    {
        Directory.CreateDirectory(_sandbox.Paths.StateDirectory);
        File.WriteAllBytes(DryRunWindow.File(_sandbox.Paths, "A23"), JsonSerializer.SerializeToUtf8Bytes(new FirstTimerRun(Core.SchemaVersion.Current, _clock.GetUtcNow().AddDays(-days)), WslCareJsonContext.Default.FirstTimerRun));
    }

    private static ActReport Answer((int Exit, string Stdout, string Stderr) worker) =>
        JsonSerializer.Deserialize(worker.Stdout, WslCareJsonContext.Default.ActReport)!;

    private static IReadOnlyList<string> Lines(string text) => [.. text.TrimEnd().Split('\n').Select(l => l.TrimEnd('\r'))];

    [Fact]
    public void A_held_run_lock_makes_the_cleanup_task_answer_busy_75_and_delete_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var old = Make("coai-old", 30, ("a.txt", 30));
        _sandbox.WriteMachineConfig("""{ "dryRun": false }""");
        StampA23DaysAgo(8);
        var stamp = File.ReadAllBytes(DryRunWindow.File(_sandbox.Paths, "A23"));
        var supervisor = new RunningSupervisor(() => _host);
        _host = Host(supervisor);

        int exit;
        string stderr;
        using (HoldTheRunLock())
        {
            (exit, _, stderr) = CliRun.Over(_host, "tasks", "run", "cleanup", "--json");
        }

        exit.Should().Be((int)ExitCode.Busy, "a busy run is 75 exactly as the watch's and the archive's: the panel reads lastResult 75 as busy (plan 3.9)");
        supervisor.Launches.Should().ContainSingle().Which.Arguments.Should().Equal("act", "A23", "--timer", "--confirm", "--json");
        Answer(supervisor.Workers.Should().ContainSingle().Subject).Result.Should().Be("busy");
        Directory.Exists(old).Should().BeTrue("a cleanup never runs beside a watch");
        File.Exists(Cursor).Should().BeFalse();
        File.ReadAllBytes(DryRunWindow.File(_sandbox.Paths, "A23")).Should().Equal(stamp, "no stamp written beyond the planted one");
        File.Exists(DryRunWindow.File(_sandbox.Paths)).Should().BeFalse();
        Lines(stderr).Should().Contain(l => l.Contains("tasks run cleanup: the worker exited 75", StringComparison.Ordinal));
    }

    [Fact]
    public void The_cleanup_supervisor_holds_no_run_lock_when_it_launches_the_worker()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var free = new List<bool>();
        var supervisor = new RunningSupervisor(() => _host) { WhileRunning = () => free.Add(RunLockIsFree()) };
        _host = Host(supervisor);

        CliRun.Over(_host, "tasks", "run", "cleanup", "--json");

        free.Should().Equal([true], "at the moment the supervisor launches the worker the run lock is free: the supervisor holds none (the worker's act takes it)");
    }

    [Fact]
    public void An_elevated_cleanup_worker_is_refused_with_81_and_deletes_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var old = Make("coai-old", 30, ("a.txt", 30));
        _sandbox.WriteMachineConfig("""{ "dryRun": false }""");
        StampA23DaysAgo(8);
        var supervisor = new RunningSupervisor(() => _host);
        _host = Host(supervisor, elevated: true);

        var (exit, _, stderr) = CliRun.Over(_host, "tasks", "run", "cleanup", "--json");

        exit.Should().Be((int)ExitCode.NotAsRoot);
        supervisor.Workers.Should().ContainSingle().Which.Stderr.Should().Contain("NOT elevated");
        Lines(stderr).Should().Contain(l => l.Contains("the worker exited 81", StringComparison.Ordinal));
        Directory.Exists(old).Should().BeTrue();
        File.Exists(Cursor).Should().BeFalse();
    }

    [Fact]
    public void While_a23s_week_runs_the_cleanup_task_previews_and_deletes_nothing_and_never_starts_the_common_week()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var old = Make("coai-old", 30, ("a.txt", 30));
        var supervisor = new RunningSupervisor(() => _host);
        _host = Host(supervisor);

        var (exit, _, stderr) = CliRun.Over(_host, "tasks", "run", "cleanup", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var report = Answer(supervisor.Workers.Should().ContainSingle().Subject);
        report.Actions.Should().ContainSingle().Which.Status.Should().Be(ActionStatus.DryRun);
        Directory.Exists(old).Should().BeTrue();
        File.Exists(DryRunWindow.File(_sandbox.Paths, "A23")).Should().BeTrue("the first task run that reaches A23's gate starts A23's own week");
        File.Exists(DryRunWindow.File(_sandbox.Paths)).Should().BeFalse("never the COMMON stamp: that is the watch's and the archive's");
        File.Exists(RunHistory.File(_sandbox.Paths)).Should().BeTrue("the run is recorded");
        var lines = Lines(stderr);
        var peek = lines.ToList().FindIndex(l => l.Contains("tasks run cleanup: A23's dry-run window:", StringComparison.Ordinal));
        var starting = lines.ToList().FindIndex(l => l.Contains("starting", StringComparison.Ordinal));
        peek.Should().BeGreaterThanOrEqualTo(0).And.BeLessThan(starting);
    }

    [Fact]
    public void The_supervisor_only_peeks_it_never_starts_a_week_for_a_run_the_worker_may_refuse()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var supervisor = new RunningSupervisor(() => _host);
        _host = Host(supervisor, elevated: true);

        CliRun.Over(_host, "tasks", "run", "cleanup", "--json");

        File.Exists(DryRunWindow.File(_sandbox.Paths, "A23")).Should().BeFalse("an elevated worker refuses itself before the engine: no week was started by the supervisor");
        File.Exists(DryRunWindow.File(_sandbox.Paths)).Should().BeFalse();
    }

    [Fact]
    public void After_a23s_week_with_dry_run_off_the_cleanup_task_deletes_the_old_allowed_tree_and_records_the_run()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var old = Make("coai-old", 30, ("a.txt", 30), (@"sub\b.txt", 30));
        var young = Make("coai-young", 30, ("fresh.txt", 0));
        var other = Make("sometool-old", 30, ("x.txt", 30));
        var claude = Make("claude", 30, ("log.txt", 30));
        _sandbox.WriteMachineConfig("""{ "dryRun": false }""");
        StampA23DaysAgo(8);
        var supervisor = new RunningSupervisor(() => _host);
        _host = Host(supervisor);

        var (exit, _, stderr) = CliRun.Over(_host, "tasks", "run", "cleanup", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var report = Answer(supervisor.Workers.Should().ContainSingle().Subject);
        var a23 = report.Actions.Should().ContainSingle().Subject;
        a23.Status.Should().Be(ActionStatus.Ran);
        a23.Run!.Count.Should().Be(1);
        Directory.Exists(old).Should().BeFalse();
        new[] { young, other, claude }.Should().OnlyContain(d => Directory.Exists(d));
        File.Exists(Cursor).Should().BeTrue();
        File.Exists(RunHistory.File(_sandbox.Paths)).Should().BeTrue();
    }

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
}
