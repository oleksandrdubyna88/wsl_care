using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// WR-D3 (PLAN_windows_runner.md §3.2, §3.3, §6): <c>tasks run &lt;watch|archive&gt; [--json]</c> through the whole program, in-process,
/// over a supervisor the test hands in — so nothing is started: the closed set of tasks and words, the worker named by this process's
/// own path with the table's arguments and the key's deadline, the worker's code passed through, exit 84 and one stderr line at the
/// deadline, the refusals. The real job is held by <c>Core.Tests/Processes/Win32JobControlTests</c>; the built binary by
/// <c>Scenarios/WindowsTasksRunFlows</c>.
/// </summary>
public sealed class TasksRunCommandTests
{
    private const string WindowsOnly = "a CliHost over a Windows layout is built on Windows only (its probe); the Linux legs run the refusal";

    public static TheoryData<string[]> FreeArgv => new()
    {
        { ["tasks", "run"] },
        { ["tasks", "run", "collect"] },
        { ["tasks", "run", "watch --timer"] },
        { ["tasks", "run", "watch", "--timer"] },
        { ["tasks", "run", "watch", "--confirm"] },
        { ["tasks", "run", "watch", "--json", "--json"] },
        { ["tasks", "run", "watch", "act", "A19"] },
        { ["tasks", "run", "--json", "watch"] },
    };

    [Theory]
    [MemberData(nameof(FreeArgv))]
    public void Free_argv_is_refused(string[] argv)
    {
        CommandLine.Parse(argv).Should().BeOfType<Request.Failed>().Which.Message.Should().Contain("tasks run").And.Contain("watch, archive");
    }

    [Theory]
    [InlineData(new[] { "tasks", "run", "watch" }, "watch", false)]
    [InlineData(new[] { "tasks", "run", "watch", "--json" }, "watch", true)]
    [InlineData(new[] { "tasks", "run", "archive", "--json" }, "archive", true)]
    public void A_task_of_the_closed_set_parses(string[] argv, string task, bool json)
    {
        var request = CommandLine.Parse(argv).Should().BeOfType<Request.TasksRun>().Subject;

        request.Task.Name.Should().Be(task);
        request.Json.Should().Be(json);
    }

    [Fact]
    public void The_run_is_refused_on_the_linux_binary()
    {
        using var root = new TempRoot("tasks-run-linux");
        var paths = new LinuxHostPaths(LinuxEnvironment.Sandboxed(root.Path));
        var clock = new FixedTimeProvider();
        var supervisor = new RecordingSupervisor(new WorkerOutcome.Exited(0));
        var host = new CliHost(paths, new Core.Files.PhysicalFileSystem(paths), clock, new RecordingCommandRunner())
        {
            Probe = new FakeProbe(HostSide.Wsl, clock),
            ProcessPath = () => "/usr/local/bin/wsl-care",
            Supervisor = supervisor,
        };

        var (exit, stdout, stderr) = CliRun.Over(host, "tasks", "run", "watch", "--json");

        exit.Should().Be((int)ExitCode.Usage);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("Windows binary");
        supervisor.Launches.Should().BeEmpty("the distro's binary starts no worker");
    }

    [Fact]
    public void The_worker_is_this_binary_s_own_watch_timer_and_its_code_is_the_exit()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new World("tasks-run-exit");
        var supervisor = new RecordingSupervisor(new WorkerOutcome.Exited(75));

        var (exit, stdout, stderr) = CliRun.Over(world.Host(supervisor), "tasks", "run", "watch", "--json");

        exit.Should().Be(75, "the worker's own code passes through (75: another run held the lock — a normal answer)");
        stdout.Should().BeEmpty("the worker's answer is on the inherited stdout; the supervisor adds nothing");
        stderr.Should().BeEmpty();
        var launch = supervisor.Launches.Should().ContainSingle().Subject;
        launch.Executable.Should().Be(World.Self);
        launch.Arguments.Should().Equal("watch", "--timer", "--json");
        launch.Deadline.Should().Be(TimeSpan.FromMinutes(10), "mcpWatchdog.runLimitMinutes, default 10");
    }

    [Fact]
    public void A_worker_past_its_deadline_exits_84_with_one_stderr_line_naming_the_deadline()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new World("tasks-run-deadline");

        var (exit, stdout, stderr) = CliRun.Over(world.Host(new RecordingSupervisor(new WorkerOutcome.DeadlinePassed(TimeSpan.FromMinutes(10)))), "tasks", "run", "watch");

        exit.Should().Be((int)ExitCode.TaskEnded).And.Be(84);
        stdout.Should().BeEmpty();
        stderr.TrimEnd().Split('\n').Should().ContainSingle()
            .Which.Should().StartWith("wsl-care: tasks run watch:").And.Contain("10 min").And.Contain("mcpWatchdog.runLimitMinutes").And.Contain("whole tree");
    }

    [Fact]
    public void A_refused_supervisor_exits_1_naming_why_and_a_cancelled_one_130()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new World("tasks-run-refused");

        var refused = CliRun.Over(world.Host(new RecordingSupervisor(new WorkerOutcome.Refused("AssignProcessToJobObject failed: error 5"))), "tasks", "run", "watch");
        var cancelled = CliRun.Over(world.Host(new RecordingSupervisor(new WorkerOutcome.Cancelled())), "tasks", "run", "watch");

        refused.Exit.Should().Be((int)ExitCode.RunFailed);
        refused.Stderr.Should().Contain("tasks run watch").And.Contain("error 5");
        cancelled.Exit.Should().Be((int)ExitCode.Interrupted);
    }

    [Fact]
    public void The_archive_task_is_refused_as_not_built_and_starts_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new World("tasks-run-archive");
        var supervisor = new RecordingSupervisor(new WorkerOutcome.Exited(0));

        var (exit, stdout, stderr) = CliRun.Over(world.Host(supervisor), "tasks", "run", "archive", "--json");

        exit.Should().Be((int)ExitCode.Usage);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("not built in this version").And.Contain("WR-D5");
        supervisor.Launches.Should().BeEmpty();
    }

    [Fact]
    public void A_host_without_its_own_path_starts_no_worker()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new World("tasks-run-nopath");
        var supervisor = new RecordingSupervisor(new WorkerOutcome.Exited(0));

        var (exit, _, stderr) = CliRun.Over(world.Host(supervisor) with { ProcessPath = static () => string.Empty }, "tasks", "run", "watch");

        exit.Should().Be((int)ExitCode.Usage);
        stderr.Should().Contain("absolute path");
        supervisor.Launches.Should().BeEmpty();
    }

    private sealed class RecordingSupervisor(WorkerOutcome outcome) : IWorkerSupervisor
    {
        public List<WorkerLaunch> Launches { get; } = [];

        public WorkerOutcome Run(WorkerLaunch launch, CancellationToken cancellationToken)
        {
            Launches.Add(launch);
            return outcome;
        }
    }

    private sealed class World(string purpose) : IDisposable
    {
        public const string Self = "C:\\Users\\user\\AppData\\Local\\wsl-care\\bin\\0123\\wsl-care.exe";

        private readonly TempRoot _root = new(purpose);

        public CliHost Host(IWorkerSupervisor supervisor)
        {
            var paths = new WindowsHostPaths(WindowsEnvironment.Sandboxed(_root.Path));
            var clock = new FixedTimeProvider();
            return new CliHost(paths, new Core.Files.PhysicalFileSystem(paths) { OwnersAreThisProcess = true }, clock, new RecordingCommandRunner())
            {
                Probe = new FakeProbe(HostSide.Windows, clock),
                ProcessPath = () => Self,
                Supervisor = supervisor,
            };
        }

        public void Dispose() => _root.Dispose();
    }
}
