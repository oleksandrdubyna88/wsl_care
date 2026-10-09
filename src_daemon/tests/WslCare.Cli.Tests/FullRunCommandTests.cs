using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Cli.Logging;
using WslCare.Core;
using WslCare.Core.Collect;
using WslCare.Core.Collectors;
using WslCare.Core.Doctor;
using WslCare.Core.Events;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Json;
using WslCare.Core.Processes;
using WslCare.Core.Records;
using WslCare.Core.Status;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary><c>collect</c>, <c>doctor</c> and <c>events follow</c> in-process: their answers, their exit codes, and
/// <c>status</c> reading back what <c>collect</c> recorded. Docker and the tools answer from the captured fixtures.</summary>
public sealed class FullRunCommandTests
{
    private static RecordingCommandRunner Tools() =>
        HealthFixture.Script(DockerFixture.Runner());

    private static CliHost Host(SandboxHost sandbox, IFileSystem files, RecordingCommandRunner runner, DateTimeOffset now)
    {
        var paths = ProcfsFixture.PathsAt(ProcfsFixture.Root);
        var clock = new FixedTimeProvider(now);
        var probe = new LinuxProbe(ProcfsFixture.LinkOverlay(new PhysicalFileSystem(paths), ProcfsFixture.Root), paths, clock);
        return new CliHost(sandbox.Paths, files, clock, runner) { Probe = probe };
    }

    private static CollectReport Collect(string stdout) =>
        JsonSerializer.Deserialize(stdout, WslCareJsonContext.Default.CollectReport) ?? throw new InvalidOperationException("collect --json printed null");

    [Fact]
    public void Collect_json_records_the_run_and_status_then_reads_its_slow_parts_back_with_their_age()
    {
        using var sandbox = new SandboxHost("collect-json");
        var runner = Tools();
        var host = Host(sandbox, sandbox.Files, runner, DockerFixture.CapturedAt);

        var (exit, stdout, stderr) = CliRun.Over(host, "collect", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var report = Collect(stdout);
        report.SchemaVersion.Should().Be(SchemaVersion.Current);
        report.Recording.Should().Be("recorded");
        report.Detail!.Sample.Vm.Memory!.Total!.Bytes.Should().Be(47_066_772L * 1024, "the fast sample is in the detail");
        report.Detail.Docker.Rows.Should().Contain(r => r.Id == "A4" && r.Available);
        report.Detail.Thresholds.Should().Contain(v => v.Id == "memory.available");
        File.Exists(RunDetailStore.Absolute(sandbox.Paths, report.DetailFile!)).Should().BeTrue();
        runner.Requests.Select(r => r.Argv[0]).Should().Contain("docker").And.OnlyContain(e => e == "docker" || e == "systemctl" || e == "journalctl" || e == "timedatectl" || e == "powershell.exe" || e == "snap" || e == "curl");

        var later = Host(sandbox, sandbox.Files, new RecordingCommandRunner(), DockerFixture.CapturedAt.AddMinutes(30));
        var (statusExit, statusOut, _) = CliRun.Over(later, "status", "--json");

        statusExit.Should().Be((int)ExitCode.Ok);
        var status = JsonSerializer.Deserialize(statusOut, WslCareJsonContext.Default.StatusReport)!;
        status.Slow.ContainerStats.Should().Match<SlowPartReport>(p => p.Available && p.RunId == report.Detail.RunId.Text && p.AgeSeconds >= 1800);
        status.ContainerStarts!.Complete.Should().BeFalse("the follower has never run here, and the count says so");
    }

    [Fact]
    public void A_full_runs_detail_carries_the_mcp_servers_and_their_three_verdicts()
    {
        // Plan §15q E7.S2d: the captured tree holds two coai-mcp servers under two claude sessions; the full run records them in
        // its embedded sample and judges the three mcp.* thresholds into its detail.
        using var sandbox = new SandboxHost("collect-mcp");
        var waits = 0;
        var host = Host(sandbox, sandbox.Files, Tools(), DockerFixture.CapturedAt) with
        {
            Wait = (_, _) =>
            {
                waits++;
                return Task.CompletedTask;
            },
        };

        var (exit, stdout, stderr) = CliRun.Over(host, "collect", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var detail = Collect(stdout).Detail!;
        detail.Thresholds.Select(v => v.Id).Should().Contain([Core.Thresholds.McpVerdicts.Instances, Core.Thresholds.McpVerdicts.Cpu, Core.Thresholds.McpVerdicts.Starts]);
        if (OperatingSystem.IsLinux())
        {
            detail.Sample.McpServers!.Count.Should().Be(2);
            // The instances come from the probe's captured tree; their CPU is read through the HOST's /proc — this sandbox has
            // none — so no instance can be measured. Since plan E14 S1 the window is waited only for an instance it CAN measure
            // and has no baseline of (before, it was waited here for two unmeasurable instances); the window's own count is
            // McpServerCollectorTests' (No_wait_when_every_instance_has_a_baseline: one window for a first sighting).
            detail.Sample.McpServers.Instances!.Should().OnlyContain(i => i.CpuBasis == "none" && !i.CpuPercent.Available);
            waits.Should().Be(0, "no instance could be measured, so there was nothing to wait for");
        }
        else
        {
            // The sandbox host of this OS is the WINDOWS layout here: the Windows binary counts its own side's servers in status only (E14 S7a).
            detail.Sample.McpServers!.Reason.Should().Be(Core.Mcp.McpServerCollector.WindowsReadsItsOwn);
            detail.Thresholds.Where(v => v.Id.StartsWith("mcp.", StringComparison.Ordinal)).Should().OnlyContain(v => v.Level == Core.Thresholds.Level.Unknown);
            waits.Should().Be(0);
        }
    }

    [Fact]
    public void Status_after_a_collect_carries_the_full_runs_own_verdict_records_with_its_run_and_their_age()
    {
        using var sandbox = new SandboxHost("collect-verdicts");
        var report = Collect(CliRun.Over(Host(sandbox, sandbox.Files, Tools(), DockerFixture.CapturedAt), "collect", "--json").Stdout);
        var detail = report.Detail!;

        var later = Host(sandbox, sandbox.Files, new RecordingCommandRunner(), DockerFixture.CapturedAt.AddMinutes(30));
        var status = JsonSerializer.Deserialize(CliRun.Over(later, "status", "--json").Stdout, WslCareJsonContext.Default.StatusReport)!;

        var carried = status.Verdicts!.Where(v => v.Basis!.Source == Core.Thresholds.VerdictSource.FullRun).ToList();
        carried.Should().NotBeEmpty();
        var basis = new Core.Thresholds.VerdictBasis(Core.Thresholds.VerdictSource.FullRun, detail.RunId.Text, detail.EndedAt, (DockerFixture.CapturedAt.AddMinutes(30) - detail.EndedAt).TotalSeconds);
        carried.Should().Equal(
            detail.Thresholds.Where(t => carried.Any(c => c.Id == t.Id)).Select(t => t with { Basis = basis }),
            "each is the record the run's detail holds, read back from the file the run wrote");
        carried.Select(v => v.Id).Should().Contain(["clock.jumps", "docker.A4", "collectors.sysstat"]);
        status.Verdicts!.Select(v => v.Id).Should().Equal(detail.Thresholds.Select(t => t.Id), "status answers every id the run recorded, in its order");
    }

    [Fact]
    public void Collect_without_json_prints_the_verdicts_and_where_the_run_was_recorded()
    {
        using var sandbox = new SandboxHost("collect-text");

        var (exit, stdout, _) = CliRun.Over(Host(sandbox, sandbox.Files, Tools(), DockerFixture.CapturedAt), "collect");

        exit.Should().Be((int)ExitCode.Ok);
        stdout.Should().Contain("wsl-care collect (").And.Contain("recorded (runs/").And.Contain("thresholds:");
    }

    [Fact]
    public void An_unprivileged_collect_prints_its_answer_says_read_only_on_stderr_and_exits_zero()
    {
        using var sandbox = new SandboxHost("collect-ro");

        var (exit, stdout, stderr) = CliRun.Over(Host(sandbox, new NotWritable(sandbox.Files), Tools(), DockerFixture.CapturedAt), "collect", "--json");

        exit.Should().Be((int)ExitCode.Ok);
        Collect(stdout).Recording.Should().Be("readOnly");
        stderr.Should().Contain("wsl-care: read-only: run as root to record");
        Directory.Exists(sandbox.Paths.StateDirectory).Should().BeFalse();
    }

    [Fact]
    public void A_collect_whose_history_line_cannot_be_written_exits_1_and_says_the_run_was_not_recorded()
    {
        using var sandbox = new SandboxHost("collect-fail");

        var (exit, stdout, stderr) = CliRun.Over(Host(sandbox, new RefusingHistoryAppends(sandbox.Files), Tools(), DockerFixture.CapturedAt), "collect", "--json");

        exit.Should().Be((int)ExitCode.RunFailed);
        Collect(stdout).Recording.Should().Be("failed");
        stderr.Should().Contain("the run was not recorded: the history line could not be written");
    }

    [Fact]
    public void A_collect_while_another_holds_the_run_lock_exits_75_and_prints_nothing()
    {
        using var sandbox = new SandboxHost("collect-busy");
        sandbox.Files.CreateDirectory(sandbox.Paths.StateDirectory);
        var held = (ExclusiveLock.Held)RunLock.TryTake(sandbox.Paths, sandbox.Files);
        using (held.Handle)
        {
            var (exit, stdout, stderr) = CliRun.Over(Host(sandbox, sandbox.Files, Tools(), DockerFixture.CapturedAt), "collect");

            exit.Should().Be((int)ExitCode.Busy);
            stdout.Should().BeEmpty();
            stderr.Should().Contain("another run is in progress");
        }
    }

    [Fact]
    public void Doctor_json_answers_exit_zero_with_its_verdict_and_checks()
    {
        using var sandbox = new SandboxHost("doctor");

        var (exit, stdout, stderr) = CliRun.Over(Host(sandbox, sandbox.Files, Tools(), DockerFixture.CapturedAt), "doctor", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var report = JsonSerializer.Deserialize(stdout, WslCareJsonContext.Default.DoctorReport)!;
        report.Healthy.Should().BeFalse("nothing has run here");
        report.Checks.Should().Contain(c => c.Id == "lastRun" && c.State == DoctorRun.Problem);
        report.Versions.Should().Contain(v => v.Component == "docker" && v.Available);
    }

    [Fact]
    public void Status_reads_the_followers_24_hour_summary_and_never_opens_a_day_file_however_many_starts_were_recorded()
    {
        // Gate finding #8: status must not re-parse the raw JSONL, or its cost grows with every start the follower ever
        // recorded. The follower (here, one --once catch-up over 200 backfilled starts) keeps a small summary; status reads it.
        using var sandbox = new SandboxHost("status-summary");
        var now = DockerFixture.CapturedAt;
        var starts = Enumerable.Range(0, 200).Select(i => DockerEventLines.Start($"c{i:D3}", $"n{i}", "postgres:17", now.AddMinutes(-(i + 1)))).ToArray();
        var follow = Tools()
            .Script(a => a is ["docker", "events", "--since", _, "--until", _, "--format", _], RecordingCommandRunner.Exited(0, DockerEventLines.Text(starts)));
        CliRun.Over(Host(sandbox, sandbox.Files, follow, now), "events", "follow", "--once").Exit.Should().Be((int)ExitCode.Ok);
        var dayFolder = new ContainerStartsStore(sandbox.Paths, sandbox.Files).Directory;
        var reads = new ReadRecordingFileSystem(sandbox.Files);

        var (exit, stdout, stderr) = CliRun.Over(Host(sandbox, reads, new RecordingCommandRunner(), now.AddMinutes(1)), "status", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var report = JsonSerializer.Deserialize(stdout, WslCareJsonContext.Default.StatusReport)!;
        report.ContainerStarts!.Starts.Should().Be(200, "the summary carries the follower's 24-hour count");
        reads.Paths.Should().NotContain(p => p.StartsWith(dayFolder, StringComparison.OrdinalIgnoreCase), "status opens no day file and lists no day folder");
    }

    /// <summary>The real file system, recording every path a read or a listing touched.</summary>
    private sealed class ReadRecordingFileSystem(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        private readonly List<string> _paths = [];

        public IReadOnlyList<string> Paths => _paths;

        public override FileReadResult ReadFile(string path)
        {
            _paths.Add(path);
            return base.ReadFile(path);
        }

        public override IReadOnlyList<string> ListFiles(string path)
        {
            _paths.Add(path);
            return base.ListFiles(path);
        }
    }

    [Fact]
    public void Events_follow_once_with_docker_down_exits_zero_and_names_why_nothing_was_recorded()
    {
        using var sandbox = new SandboxHost("events-once");
        var runner = new RecordingCommandRunner { Default = new CommandOutcome.FailedToStart("No such file or directory") };

        var (exit, stdout, _) = CliRun.Over(Host(sandbox, sandbox.Files, runner, DockerFixture.CapturedAt), "events", "follow", "--once");

        exit.Should().Be((int)ExitCode.Ok);
        stdout.Should().Contain("Docker could not be read").And.Contain("NotInstalled");
        new ContainerStartsStore(sandbox.Paths, sandbox.Files).ReadAll().Select(l => l.GetType().Name).Should().Equal(nameof(CoverageLine.FollowerStarted), nameof(CoverageLine.FollowerStopped));
    }

    [Fact]
    public void Events_follow_by_a_process_that_may_not_write_the_state_exits_1_and_records_nothing()
    {
        using var sandbox = new SandboxHost("events-ro");

        var (exit, _, stderr) = CliRun.Over(Host(sandbox, new NotWritable(sandbox.Files), new RecordingCommandRunner(), DockerFixture.CapturedAt), "events", "follow");

        exit.Should().Be((int)ExitCode.RunFailed);
        stderr.Should().Contain("run it as root");
    }

    [Fact]
    public void A_second_follower_exits_75()
    {
        using var sandbox = new SandboxHost("events-busy");
        sandbox.Files.CreateDirectory(sandbox.Paths.StateDirectory);
        var store = new ContainerStartsStore(sandbox.Paths, sandbox.Files);
        var held = (ExclusiveLock.Held)sandbox.Files.TryLockExclusive(store.FollowerLock);
        using (held.Handle)
        {
            CliRun.Over(Host(sandbox, sandbox.Files, new RecordingCommandRunner(), DockerFixture.CapturedAt), "events", "follow", "--once").Exit.Should().Be((int)ExitCode.Busy);
        }
    }

    [Fact]
    public void Events_follow_stopped_by_a_signal_exits_zero_with_its_stop_marker()
    {
        using var sandbox = new SandboxHost("events-signal");
        using var signal = new CancellationTokenSource();
        var runner = HealthFixture.Script(DockerFixture.Runner())
            .Script(a => a is ["docker", "events", "--since", _, "--until", _, "--format", _], RecordingCommandRunner.Exited(0, string.Empty))
            .Stream(new StreamScript([], RecordingCommandRunner.Exited(0)) { Then = signal.Cancel });

        var (exit, _, _) = CliRun.Over(Host(sandbox, sandbox.Files, runner, DockerFixture.CapturedAt), Serilog.Core.Logger.None, signal.Token, "events", "follow");

        exit.Should().Be((int)ExitCode.Ok, "a signal is the follower's normal way to end (plan §15b #8)");
        new ContainerStartsStore(sandbox.Paths, sandbox.Files).ReadAll()[^1].Should().BeOfType<CoverageLine.FollowerStopped>();
    }

    [Fact]
    public void A_run_that_may_not_write_the_system_log_directory_logs_to_the_users_own()
    {
        using var sandbox = new SandboxHost("log-root");
        var host = new CliHost(sandbox.Paths, new NotWritable(sandbox.Files), new FixedTimeProvider(), new RecordingCommandRunner());

        WslCareLogging.LogRoot(host).Should().Be(sandbox.Paths.UserLogDirectory);
        WslCareLogging.LogRoot(host with { Files = sandbox.Files }).Should().Be(sandbox.Paths.LogDirectory);
    }

    private sealed class NotWritable(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public override WriteAccess ProbeWriteAccess(string directory) => new WriteAccess.NotWritable($"{directory}: permission denied (test)");
    }

    // Retro gate over PR #5 (code round, F3): a full run can spend minutes in Docker's disk figures and the folder walks, and
    // said nothing until it ended — a person at a terminal could not tell working from stuck.
    [Fact]
    public void Collect_says_it_is_measuring_before_the_first_slow_command_starts()
    {
        using var sandbox = new SandboxHost("collect-progress");
        var runner = Tools();
        var host = Host(sandbox, sandbox.Files, runner, DockerFixture.CapturedAt);
        var seen = new CommandsAtEachLogLine(runner);
        using var logger = new Serilog.LoggerConfiguration().MinimumLevel.Information().WriteTo.Sink(seen).CreateLogger();

        var (exit, _, stderr) = CliRun.Over(host, logger, CancellationToken.None, "collect", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        seen.Lines.Should().Contain(l => l.Message.Contains("measuring", StringComparison.Ordinal) && l.CommandsSoFar == 0,
            "the note comes before Docker or any other tool is asked anything");
    }

    [Fact]
    public void The_measuring_line_reaches_stderr_and_the_run_file_through_the_real_logger()
    {
        using var sandbox = new SandboxHost("collect-progress-real");
        var host = Host(sandbox, sandbox.Files, Tools(), DockerFixture.CapturedAt);
        var console = new StringWriter();

        using (var logger = WslCareLogging.Start(host, host.LoadConfig(), "wsl-care", console))
        {
            CliRun.Over(host, logger, CancellationToken.None, "collect", "--json").Exit.Should().Be((int)ExitCode.Ok);
        }

        console.ToString().Should().Contain("collect: measuring", "the console sink is the stderr a person watches");
        string.Concat(Directory.GetFiles(sandbox.Paths.LogDirectory, "*.log", SearchOption.AllDirectories).Select(File.ReadAllText))
            .Should().Contain("collect: measuring", "the run file keeps it too");
    }

    private sealed class CommandsAtEachLogLine(RecordingCommandRunner runner) : Serilog.Core.ILogEventSink
    {
        public List<(string Message, int CommandsSoFar)> Lines { get; } = [];

        public void Emit(Serilog.Events.LogEvent logEvent) => Lines.Add((logEvent.RenderMessage(System.Globalization.CultureInfo.InvariantCulture), runner.Requests.Count));
    }
}
