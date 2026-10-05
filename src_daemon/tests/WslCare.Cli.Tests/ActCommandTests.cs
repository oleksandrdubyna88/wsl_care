using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// <c>act &lt;A#&gt;[,&lt;A#&gt;…] (--preview or --confirm) [--json]</c> in-process: the parse, root first (refused whole before the lock
/// or any state, plan §15c #0), every exit code, the JSON answer. The distro's layout over a temporary root; journalctl is a
/// recording runner under the product policy.
/// </summary>
public sealed class ActCommandTests : IDisposable
{
    private static readonly ProcessPrivilege Root = new(true, "a test says so");
    private static readonly ProcessPrivilege NotRoot = new(false, "this process does not run as root (a test)");

    private readonly LinuxSandbox _sandbox = new("act-cli");
    private readonly RecordingCommandRunner _runner = new RecordingCommandRunner { Policy = CommandPolicy.Product }
        .Script(Core.Systemd.SystemdCommands.JournalDiskUsage.Argv, 0, "Archived and active journals take up 1.5G in the file system.");

    public ActCommandTests()
    {
        _sandbox.Write("/etc/passwd", "root:x:0:0::/root:/bin/bash\nme:x:1000:1000::/home/me:/bin/bash\n");
    }

    public void Dispose() => _sandbox.Dispose();

    private CliHost Host(ProcessPrivilege privilege, IFileSystem? files = null) =>
        new(_sandbox.Paths, files ?? _sandbox.Files, new FixedTimeProvider(), _runner) { Privilege = privilege, Processes = new FakeProcessTable() };

    private static ActReport Report(string stdout) => JsonSerializer.Deserialize(stdout, WslCareJsonContext.Default.ActReport)!;

    [Theory]
    [InlineData("act")]
    [InlineData("act", "--preview")]
    [InlineData("act", "A10")]
    [InlineData("act", "A10", "--preview", "--confirm")]
    [InlineData("act", "A10", "--preview", "--preview")]
    [InlineData("act", "A10", "--force")]
    [InlineData("act", "A99", "--preview")]
    [InlineData("act", "A10,A10", "--confirm")]
    public void An_act_without_known_actions_and_exactly_one_of_preview_and_confirm_is_a_usage_refusal(params string[] argv)
    {
        CommandLine.Parse(argv).Should().BeOfType<Request.Failed>();
    }

    /// <summary>§15j m2: the panel's mark and the timer's are exclusive — E3 let the timer win when both were given (more gates,
    /// not fewer); a run that claims to be both is now refused as the usage error it is.</summary>
    [Theory]
    [InlineData("act", "A10", "--confirm", "--manual", "--timer")]
    [InlineData("act", "A10", "--preview", "--timer", "--json", "--manual")]
    public void Manual_and_timer_together_are_refused_naming_both(params string[] argv)
    {
        var failed = CommandLine.Parse(argv).Should().BeOfType<Request.Failed>().Subject;

        failed.Message.Should().Contain("--manual").And.Contain("--timer").And.Contain("not both");
    }

    /// <summary>§15f #3, §15j: every act answer names the build that answered — the text --version prints.</summary>
    [Fact]
    public void Every_act_answer_names_the_product_version_exactly_as_version_prints_it()
    {
        var (_, preview, previewErr) = CliRun.Over(Host(Root), "act", "A10", "--preview", "--json");
        var (_, run, runErr) = CliRun.Over(Host(Root), "act", "A10", "--confirm", "--json");

        Report(preview).ProductVersion.Should().Be(Program.VersionText, previewErr);
        Report(run).ProductVersion.Should().Be(Program.VersionText, runErr);
    }

    [Fact]
    public void An_act_parses_its_actions_in_the_order_given_and_its_flags()
    {
        var act = CommandLine.Parse(["act", "A10,A5", "--json", "--confirm"]).Should().BeOfType<Request.Act>().Subject;

        act.Ids.Select(i => i.Text).Should().Equal("A10", "A5");
        act.Confirm.Should().BeTrue();
        act.Json.Should().BeTrue();
    }

    [Theory]
    [InlineData("--preview")]
    [InlineData("--confirm")]
    public void An_unprivileged_act_is_refused_whole_before_the_lock_or_any_state_is_touched(string mode)
    {
        var (exit, stdout, stderr) = CliRun.Over(Host(NotRoot), "act", "A10", mode, "--json");

        exit.Should().Be((int)ExitCode.NeedsRoot);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("needs root").And.Contain("nothing was done");
        Directory.Exists(_sandbox.Paths.StateDirectory).Should().BeFalse("no state: not running.json, not a history line, not the dry-run stamp");
        File.Exists(_sandbox.Paths.RunLockFile).Should().BeFalse("not even the lock file");
        _runner.Requests.Should().BeEmpty("not even a read command");
    }

    [Fact]
    public void A_preview_prints_each_action_s_live_preview_as_json_and_writes_nothing()
    {
        var (exit, stdout, stderr) = CliRun.Over(Host(Root), "act", "A10", "--preview", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var report = Report(stdout);
        report.Mode.Should().Be("preview");
        report.Result.Should().Be("previewed");
        report.Actions.Single().Should().Match<ActionOutcome>(a => a.Id == "A10" && a.Status == ActionStatus.Previewed && a.Preview!.Available);
        Directory.Exists(_sandbox.Paths.StateDirectory).Should().BeFalse();
        _runner.Commands.Should().Equal("journalctl --disk-usage");
    }

    [Fact]
    public void A_confirmed_act_runs_records_and_answers_with_the_measured_result()
    {
        var (exit, stdout, stderr) = CliRun.Over(Host(Root), "act", "A10", "--confirm", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var report = Report(stdout);
        report.Result.Should().Be("recorded");
        report.DryRun.Should().BeFalse("a button or the CLI never dry-runs");
        report.Actions.Single().Status.Should().Be(ActionStatus.Ran);
        report.Actions.Single().Run!.FreedBasis.Should().Contain("gone after");
        RunHistory.Read(_sandbox.Paths, _sandbox.Files).Records.Single().DetailPath.Should().Be(report.DetailFile);
        _runner.Commands.Should().Equal("journalctl --disk-usage", "journalctl --vacuum-time=30d");
    }

    [Fact]
    public void The_text_answer_names_each_action_its_status_and_the_run()
    {
        var (exit, stdout, _) = CliRun.Over(Host(Root), "act", "A10", "--confirm");

        exit.Should().Be((int)ExitCode.Ok);
        stdout.Should().Contain("wsl-care act, run ").And.Contain("recorded (runs/").And.Contain("A10").And.Contain("ran");
    }

    [Fact]
    public void A_failed_action_exits_3_and_names_it_while_the_run_is_still_recorded()
    {
        _runner.Script(["journalctl", "--vacuum-time=30d"], 1, string.Empty, "Failed to vacuum: Permission denied");

        var (exit, _, stderr) = CliRun.Over(Host(Root), "act", "A10", "--confirm");

        exit.Should().Be((int)ExitCode.ActionFailed);
        stderr.Should().Contain("A10:").And.Contain("Permission denied");
        RunHistory.Read(_sandbox.Paths, _sandbox.Files).Records.Single().Actions.Single().Status.Should().Be(ActionStatus.Failed);
    }

    [Fact]
    public void An_act_while_another_run_holds_the_lock_exits_75_and_does_nothing()
    {
        var held = (ExclusiveLock.Held)RunLock.TryTake(_sandbox.Paths, _sandbox.Files);
        using (held.Handle)
        {
            var (exit, stdout, stderr) = CliRun.Over(Host(Root), "act", "A10", "--confirm", "--json");

            exit.Should().Be((int)ExitCode.Busy);
            Report(stdout).Result.Should().Be("busy");
            stderr.Should().Contain("busy:");
        }

        _runner.Requests.Should().BeEmpty();
    }

    [Fact]
    public void An_act_meeting_a_wedged_run_exits_76_and_kills_nothing()
    {
        var processes = new FakeProcessTable().Alive(999, FixedTimeProvider.DefaultNow.AddHours(-1));
        Directory.CreateDirectory(_sandbox.Paths.StateDirectory);
        var wedged = new RunningFile(1, RunId.New(FixedTimeProvider.DefaultNow.AddMinutes(-9), 999), RunTrigger.Timer, ["A10"], "A10", 999, FixedTimeProvider.DefaultNow.AddHours(-1), FixedTimeProvider.DefaultNow.AddMinutes(-9), FixedTimeProvider.DefaultNow.AddMinutes(-5));
        File.WriteAllText(RunningState.File(_sandbox.Paths), JsonSerializer.Serialize(wedged, WslCareJsonContext.Default.RunningFile));

        var (exit, _, stderr) = CliRun.Over(Host(Root) with { Processes = processes }, "act", "A10", "--confirm");

        exit.Should().Be((int)ExitCode.Wedged);
        stderr.Should().Contain("wedged").And.Contain("nothing was killed");
    }

    [Fact]
    public void An_action_this_build_does_not_hold_is_refused_by_name()
    {
        var (exit, _, stderr) = CliRun.Over(Host(Root), "act", "A13", "--preview");

        exit.Should().Be((int)ExitCode.Usage);
        stderr.Should().Contain("A13 is not built in this release; act holds: " + string.Join(", ", ActionRegistry.Product.Actions.Select(a => a.Id.Text)));
    }

    [Fact]
    public void A_distro_action_asked_of_the_windows_binary_is_refused_naming_the_side()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows binary's side: its probe reads Win32 counters, so this runs on the Windows legs");
        using var windows = new SandboxHost("act-windows-side");
        var host = new CliHost(windows.Paths, windows.Files, new FixedTimeProvider(), _runner) { Privilege = Root };

        var (exit, _, stderr) = CliRun.Over(host, "act", "A10", "--preview");

        exit.Should().Be((int)ExitCode.Usage);
        stderr.Should().Contain("A10 runs inside the WSL distro, not on this side (the Windows binary)");
    }

    [Fact]
    public void A_confirmed_act_under_an_invalid_configuration_layer_exits_78_and_runs_nothing()
    {
        _sandbox.Write("/home/me/.config/wsl-care/config.json", "{ \"journal\": { \"keepDays\": 0 } }");

        var (exit, _, stderr) = CliRun.Over(Host(Root), "act", "A10", "--confirm");

        exit.Should().Be((int)ExitCode.ObserveOnly);
        stderr.Should().Contain("observe-only");
        _runner.Requests.Should().BeEmpty();
    }

    [Fact]
    public void The_help_names_the_act_verb_and_the_actions_this_build_holds()
    {
        CommandLine.HelpText.Should().Contain("act <A#>[,<A#>...] (--preview or --confirm) [--manual or --timer] [--detach] [--volume <name>]... [--only <file or ->] [--json]")
            .And.Contain("act --request <runId>").And.Contain("act --stop <runId> [--json]").And.Contain("collect [--timer or --detach] [--json]")
            .And.Contain("\"act\" holds these actions: " + string.Join(", ", ActionRegistry.Product.Actions.Select(a => a.Id.Text)));
    }

    // ---------- E3.S2: the panel's mark and A4's shown list ----------

    [Fact]
    public void A_button_run_marked_manual_is_recorded_with_the_manual_trigger()
    {
        var (exit, stdout, stderr) = CliRun.Over(Host(Root), "act", "A10", "--confirm", "--manual", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        Report(stdout).Result.Should().Be("recorded");
        RunHistory.Read(_sandbox.Paths, _sandbox.Files).Records.Single().Trigger.Should().Be(RunTrigger.Manual);
    }

    [Theory]
    [InlineData("A4", "--volume", "not-a-volume")]
    [InlineData("A10", "--volume", "6300b7160d6ba80230316f78016e24424d0b7b611876e5e7c036ac405ee905ac")]
    [InlineData("A4", "--volume")]
    [InlineData("A4", "--only")]
    [InlineData("A4", "--only", "a", "--only", "b")]
    public void A_shown_list_belongs_to_a4_and_holds_only_anonymous_volume_names(params string[] rest)
    {
        CommandLine.Parse(["act", rest[0], "--confirm", .. rest.Skip(1)]).Should().BeOfType<Request.Failed>();
    }

    [Fact]
    public void A_shown_list_parses_into_the_request_with_the_manual_mark()
    {
        var volume = new string('a', 64);

        var act = CommandLine.Parse(["act", "A5,A4", "--confirm", "--manual", "--volume", volume, "--only", "/tmp/shown.txt"]).Should().BeOfType<Request.Act>().Subject;

        act.Manual.Should().BeTrue();
        act.Volumes.Should().Equal(volume);
        act.OnlyFile.Should().Be("/tmp/shown.txt");
    }

    [Fact]
    public void An_only_file_with_a_line_that_is_not_a_volume_name_is_refused_naming_the_line_never_its_content()
    {
        var file = _sandbox.Write("/tmp/shown.txt", $"{new string('a', 64)}\nsecret-looking text\n");

        var (exit, stdout, stderr) = CliRun.Over(Host(Root), "act", "A4", "--confirm", "--only", file);

        exit.Should().Be((int)ExitCode.Usage);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("line 2").And.NotContain("secret-looking");
        _runner.Requests.Should().BeEmpty("nothing was read from Docker, nothing removed");
    }

    [Fact]
    public void An_only_file_that_is_a_directory_is_refused_as_not_a_regular_file()
    {
        var folder = _sandbox.Paths.DistroPath("/tmp/shown-folder");
        Directory.CreateDirectory(folder);

        var (exit, stdout, stderr) = CliRun.Over(Host(Root), "act", "A4", "--confirm", "--only", folder);

        exit.Should().Be((int)ExitCode.Usage);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("not a regular file");
        _runner.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_only_file_that_is_a_fifo_is_refused_at_once_never_waited_on()
    {
        // act reads --only as ROOT: a FIFO with no writer would hold the open forever (and a device could stream forever),
        // so only a regular file is read, and only up to the cap.
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a FIFO is a Linux file: run in WSL or on the Linux legs");
        var fifo = _sandbox.Paths.DistroPath("/tmp/shown.fifo");
        Directory.CreateDirectory(Path.GetDirectoryName(fifo)!);
        (await ChildProcess.RunAsync("mkfifo", [fifo], new Dictionary<string, string?>())).Exit.Should().Be(0);

        var act = Task.Run(() => CliRun.Over(Host(Root), "act", "A4", "--confirm", "--only", fifo), TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(act, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)) == act;
        if (!finished)
        {
            await using var writer = new FileStream(fifo, FileMode.Open, FileAccess.Write); // releases the blocked reader
        }

        finished.Should().BeTrue("a FIFO is refused, never waited on");
        var (exit, _, stderr) = await act;
        exit.Should().Be((int)ExitCode.Usage);
        stderr.Should().Contain("not a regular file");
    }

    [Fact]
    public void As_root_working_for_the_target_user_config_set_refuses_rather_than_leave_a_root_owned_file_in_their_home()
    {
        var host = Host(Root) with { HomeOwner = new HomeOwner.Target(new TargetUser("me", 1000, "/home/me"), "test") };

        var (exit, _, stderr) = CliRun.Over(host, "config", "set", "dryRun", "false");

        exit.Should().Be((int)ExitCode.Usage);
        stderr.Should().Contain("run them as me, not as root");
        File.Exists(_sandbox.Paths.UserConfigFile).Should().BeFalse();
    }
}
