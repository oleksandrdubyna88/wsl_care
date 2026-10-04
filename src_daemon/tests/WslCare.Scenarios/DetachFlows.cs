using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core;
using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Docker;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Records;
using WslCare.Core.Systemd;
using WslCare.FakeTool;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// A DETACHED run end to end (E6.S1, plan §15j B2, §15k #0 / #2 / #15; the §16 E6.S1 acceptance): the BUILT CLI over a sandbox
/// with root claimed and a fake <c>systemctl</c>. <c>--detach</c> answers <c>accepted</c> and starts the template unit; the
/// unit's <c>act --request</c> (run here by the test, as systemd would) records under the pre-allocated run id; the timer
/// holding the lock makes the request record <c>refused</c> and remove itself; a stale request whose unit is gone is swept
/// <c>interrupted</c> by the next root run; without systemd a detach is refused with 69; a child that never exits is ended by
/// its own ceiling and the run still records and releases the lock.
/// </summary>
public sealed class DetachFlows
{
    private const string LinuxOnly = "a detached run is the distro's: covered on the Linux legs (and by hand in WSL)";

    private static ScenarioHome Home(string purpose)
    {
        var home = new ScenarioHome(purpose) { ClaimsRoot = true };
        home.Script(SystemdCommands.Journalctl, SystemdCommands.JournalDiskUsage.Arguments, 0, $"docker/{DockerFixture.Name}/journalctl-disk-usage.out");
        home.Script(SystemdCommands.Journalctl, ["--vacuum-time=30d"], 0, stderr: "Vacuuming done, freed 0B of archived journals from /var/log/journal.");
        home.Answer(new FakeAnswer(SystemdCommands.Systemctl, ["start", "--no-block"], 0, string.Empty, string.Empty) { Prefix = true });
        if (home.Paths is LinuxHostPaths linux)
        {
            Directory.CreateDirectory(linux.DistroPath("/run/systemd/system"));
        }

        return home;
    }

    private static PhysicalFileSystem Files(ScenarioHome home) => new(home.Paths);

    private static IReadOnlyList<RunRecord> History(ScenarioHome home) => RunHistory.Read(home.Paths, Files(home)).Records;

    /// <summary>The request files by name. Not through the hardened reader: that trusts root (or the euid of a process that
    /// claims root in a sandbox), and this test process claims nothing — the CLI child is what reads them for real.</summary>
    private static IReadOnlyList<string> Requests(ScenarioHome home) =>
        Directory.Exists(RunRequests.Directory(home.Paths)) ? [.. Directory.EnumerateFiles(RunRequests.Directory(home.Paths)).Select(Path.GetFileName).OfType<string>()] : [];

    private static HandOffReport Accepted(ChildResult result)
    {
        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        var answer = JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.HandOffReport)!;
        answer.Result.Should().Be("accepted");
        return answer;
    }

    [Fact]
    public async Task An_accepted_detach_starts_its_unit_and_the_unit_s_request_run_records_under_the_answered_run_id()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);
        using var home = Home("detach-accepted");

        var answer = Accepted(await home.RunAsync("act", "A10", "--confirm", "--manual", "--detach", "--json"));

        home.Calls.Select(c => c.Display).Should().Equal($"systemctl start --no-block wsl-care-act@{answer.RunId}.service");
        Requests(home).Should().Equal($"{answer.RunId}.json");

        var run = await home.RunAsync("act", "--request", answer.RunId);

        run.Exit.Should().Be((int)ExitCode.Ok, run.Stderr);
        var line = History(home).Should().ContainSingle().Subject;
        line.RunId.Text.Should().Be(answer.RunId, "the panel follows the run id --detach answered");
        line.Trigger.Should().Be(RunTrigger.Manual);
        line.Outcome.Should().Be(RunOutcome.Completed);
        Requests(home).Should().BeEmpty();
        File.Exists(RunningState.File(home.Paths)).Should().BeFalse();
        home.Calls.Select(c => c.Display).Should().Contain("journalctl --vacuum-time=30d");
    }

    [Fact]
    public async Task A_request_whose_unit_meets_the_timer_s_lock_records_refused_and_removes_itself()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);
        using var home = Home("detach-refused");
        var answer = Accepted(await home.RunAsync("act", "A10", "--confirm", "--detach", "--json"));

        ChildResult run;
        var held = (ExclusiveLock.Held)RunLock.TryTake(home.Paths, Files(home));
        using (held.Handle)
        {
            run = await home.RunAsync("act", "--request", answer.RunId);
        }

        run.Exit.Should().Be((int)ExitCode.Busy, "a recorded refusal — SuccessExitStatus counts it as no failure");
        var line = History(home).Should().ContainSingle().Subject;
        line.RunId.Text.Should().Be(answer.RunId);
        line.Outcome.Should().Be(RunOutcome.Refused);
        line.Reason.Should().Contain("busy:");
        Requests(home).Should().BeEmpty();
        home.Calls.Should().NotContain(c => c.Display.Contains("--vacuum-time", StringComparison.Ordinal), "nothing ran");
    }

    [Fact]
    public async Task A_stale_request_whose_unit_is_gone_is_swept_interrupted_by_the_next_root_collect()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);
        using var home = Home("detach-swept");
        var created = DateTimeOffset.UtcNow.AddMinutes(-30);
        var stale = new RunRequestFile(SchemaVersion.Current, RunId.New(created, 4321), "act", ["A10"], RunTrigger.Manual, created);
        RunRequests.Create(home.Paths, Files(home), stale).Should().BeOfType<ExclusiveCreate.Created>();
        home.Answer(new FakeAnswer(SystemdCommands.Systemctl, ["show", "--property=ActiveState", "--property=Job", $"wsl-care-act@{stale.RunId}.service"], 0, home.WriteFile("show.out", "ActiveState=inactive\nJob=\n"), string.Empty));

        var collect = await home.RunAsync("collect", "--json");

        collect.Exit.Should().Be((int)ExitCode.Ok, collect.Stderr);
        var swept = History(home).Should().Contain(r => r.RunId == stale.RunId).Subject;
        swept.Outcome.Should().Be(RunOutcome.Interrupted);
        swept.Reason.Should().Contain("swept: the detached run never recorded itself").And.Contain("is inactive with no queued job");
        Requests(home).Should().BeEmpty();
        History(home).Should().HaveCount(2, "the swept line and the collect's own");
    }

    /// <summary>§15k #14: <c>requests/</c> is 0755 and a request 0644 whatever umask root runs under — the unprivileged
    /// <c>status</c> reads them, and a root shell with <c>umask 077</c> must not make the queued state invisible.</summary>
    [Fact]
    public async Task Under_a_restrictive_umask_the_request_folder_is_0755_and_the_request_0644()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip(LinuxOnly);
            return;
        }

        using var home = Home("detach-umask");

        var result = await ChildProcess.RunAsync(
            "/bin/sh",
            ["-c", "umask 077 && exec \"$0\" \"$@\"", ChildProcess.BesideTheTests(CommandLine.BinaryName), "act", "A10", "--confirm", "--detach", "--json"],
            home.Environment,
            home.WorkingDirectory);

        var answer = Accepted(result);
        File.GetUnixFileMode(RunRequests.Directory(home.Paths)).Should().Be(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.GetUnixFileMode(Path.Combine(RunRequests.Directory(home.Paths), $"{answer.RunId}.json")).Should().Be(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    [Fact]
    public async Task Without_systemd_a_detach_is_refused_with_69_and_nothing_is_written_or_started()
    {
        using var home = new ScenarioHome("detach-no-systemd") { ClaimsRoot = true };

        var result = await home.RunAsync("collect", "--detach", "--json");

        result.Exit.Should().Be((int)ExitCode.DetachUnavailable, result.Stderr);
        result.Stdout.Should().BeEmpty();
        CliStderr.Of(result).Messages.Should().ContainSingle(m => m.Contains("needs systemd", StringComparison.Ordinal));
        Directory.Exists(RunRequests.Directory(home.Paths)).Should().BeFalse();
        File.Exists(RunHistory.File(home.Paths)).Should().BeFalse("no synchronous fallback ran");
        home.Calls.Should().BeEmpty();
    }

    /// <summary>§15k #0: a confirm is never time-killed as a whole, but every command it starts has its own ceiling with a tree
    /// kill — a child that never exits ends the STEP, the run records and the lock is released. The never-exiting child is
    /// <c>journalctl --disk-usage</c> (a 15 s ceiling) rather than a docker listing (30 s) to keep the flow short; the
    /// mechanism — the runner's ceiling — is the same for every tool.</summary>
    [Fact]
    public async Task A_child_that_never_exits_is_ended_by_its_own_ceiling_and_the_detached_run_still_records_and_releases_the_lock()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);
        using var home = new ScenarioHome("detach-hang") { ClaimsRoot = true };
        home.Script(SystemdCommands.Journalctl, SystemdCommands.JournalDiskUsage.Arguments, 0, delayMilliseconds: 600_000);
        home.Answer(new FakeAnswer(SystemdCommands.Systemctl, ["start", "--no-block"], 0, string.Empty, string.Empty) { Prefix = true });
        Directory.CreateDirectory(((LinuxHostPaths)home.Paths).DistroPath("/run/systemd/system"));
        var answer = Accepted(await home.RunAsync("act", "A10", "--confirm", "--detach", "--json"));

        var started = DateTime.UtcNow;
        var run = await home.RunAsync("act", "--request", answer.RunId);

        (DateTime.UtcNow - started).Should().BeLessThan(SystemdCommands.Ceiling + TimeSpan.FromSeconds(30), "the step's ceiling ended it, not the test's timeout");
        var line = History(home).Should().ContainSingle().Subject;
        line.RunId.Text.Should().Be(answer.RunId, run.Stderr);
        line.Actions.Should().ContainSingle().Which.Id.Should().Be("A10");
        Requests(home).Should().BeEmpty();
        File.Exists(RunningState.File(home.Paths)).Should().BeFalse();
        var again = RunLock.TryTake(home.Paths, Files(home));
        again.Should().BeOfType<ExclusiveLock.Held>("the lock was released");
        ((ExclusiveLock.Held)again).Handle.Dispose();
    }
}
