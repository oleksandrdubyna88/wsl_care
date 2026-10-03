using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
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
/// <c>wsl-care act &lt;A#&gt;[,&lt;A#&gt;…] (--preview or --confirm) [--json]</c> end to end (E3.S1): the BUILT CLI over a sandbox with a
/// fake <c>journalctl</c> alone on its PATH. Unprivileged it refuses whole and writes nothing; with root CLAIMED inside the
/// sandbox (<see cref="ScenarioHome.ClaimsRoot"/>) it previews and runs the reference action A10 — whose "vacuum" is the fake
/// answering, so nothing real is ever vacuumed — and records the run; and it shares ONE lock with <c>collect</c>.
/// </summary>
public sealed class ActFlows
{
    private static ScenarioHome Journal(string purpose, bool claimsRoot)
    {
        var home = new ScenarioHome(purpose) { ClaimsRoot = claimsRoot };
        home.Script(SystemdCommands.Journalctl, SystemdCommands.JournalDiskUsage.Arguments, 0, $"docker/{DockerFixture.Name}/journalctl-disk-usage.out");
        home.Script(SystemdCommands.Journalctl, ["--vacuum-time=30d"], 0, stderr: "Vacuuming done, freed 0B of archived journals from /var/log/journal.");
        return home;
    }

    private static ActReport Report(ChildResult result) =>
        JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.ActReport) ?? throw new InvalidOperationException($"act printed no report: {result.Stderr}");

    [Fact]
    public async Task An_unprivileged_act_is_refused_whole_and_leaves_no_state_no_lock_and_no_command_behind()
    {
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "this account is root (or an elevated Windows account): the child would be too");
        using var home = Journal("act-unprivileged", claimsRoot: false);

        var result = await home.RunAsync("act", "A10", "--confirm", "--json");

        result.Exit.Should().Be((int)ExitCode.NeedsRoot, result.Stderr);
        CliStderr.Of(result).Messages.Should().ContainSingle(m => m.Contains("needs root", StringComparison.Ordinal));
        result.Stdout.Should().BeEmpty();
        // On Windows the run LOG lives under the state directory, so the directory itself may exist; no state file does.
        File.Exists(RunningState.File(home.Paths)).Should().BeFalse("no running.json");
        File.Exists(RunHistory.File(home.Paths)).Should().BeFalse("no history line");
        File.Exists(DryRunWindow.File(home.Paths)).Should().BeFalse("no dry-run stamp");
        Directory.Exists(RunDetailStore.Root(home.Paths)).Should().BeFalse("no run detail");
        File.Exists(home.Paths.RunLockFile).Should().BeFalse("not even the lock file");
        home.Calls.Should().BeEmpty("not even a read command");
    }

    [Fact]
    public async Task With_root_claimed_a_preview_reads_the_journal_s_size_and_writes_nothing()
    {
        using var home = Journal("act-preview", claimsRoot: true);

        var result = await home.RunAsync("act", "A10", "--preview", "--json");

        if (home.Paths.Side == HostSide.Windows)
        {
            result.Exit.Should().Be((int)ExitCode.Usage, "A10 is the distro's action; the Windows binary names the side");
            CliStderr.Of(result).Messages.Should().ContainSingle(m => m.Contains("runs inside the WSL distro", StringComparison.Ordinal));
            return;
        }

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        var report = Report(result);
        report.Result.Should().Be("previewed");
        report.Actions.Single().Preview!.Facts[Core.Actions.JournalVacuum.JournalBytesFact].Should().BeGreaterThan(0);
        home.Calls.Select(c => c.Display).Should().Equal("journalctl --disk-usage");
        Directory.Exists(home.Paths.StateDirectory).Should().BeFalse("a preview touches no state");
    }

    [Fact]
    public async Task With_root_claimed_a_confirmed_act_runs_a10_through_the_fake_and_records_detail_then_history()
    {
        using var home = Journal("act-confirm", claimsRoot: true);
        Assert.SkipWhen(home.Paths.Side == HostSide.Windows, "A10 runs inside the distro: covered on the Linux legs (and by hand in WSL)");

        var result = await home.RunAsync("act", "A10", "--confirm", "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        var report = Report(result);
        report.Result.Should().Be("recorded");
        report.Actions.Single().Status.Should().Be(ActionStatus.Ran);
        home.Calls.Select(c => c.Display).Should().Equal("journalctl --disk-usage", "journalctl --vacuum-time=30d");
        var files = new PhysicalFileSystem(home.Paths);
        var line = RunHistory.Read(home.Paths, files).Records.Should().ContainSingle().Subject;
        line.Detail.Should().Be(report.DetailFile);
        line.Actions.Single().Status.Should().Be("ran");
        File.Exists(RunDetailStore.Absolute(home.Paths, report.DetailFile!)).Should().BeTrue();
        File.Exists(RunningState.File(home.Paths)).Should().BeFalse("running.json goes when the run ends");
    }

    [Fact]
    public async Task One_lock_for_collect_and_act_the_second_one_refuses_with_75_and_waits_for_nothing()
    {
        using var home = Journal("act-lock", claimsRoot: true);
        var files = new PhysicalFileSystem(home.Paths);
        var held = (ExclusiveLock.Held)RunLock.TryTake(home.Paths, files);
        using (held.Handle)
        {
            var collect = await home.RunAsync("collect", "--json");

            collect.Exit.Should().Be((int)ExitCode.Busy, collect.Stderr);
            if (home.Paths.Side == HostSide.Wsl)
            {
                var act = await home.RunAsync("act", "A10", "--confirm", "--json");

                act.Exit.Should().Be((int)ExitCode.Busy, act.Stderr);
                Report(act).Result.Should().Be("busy");
            }
        }

        home.Calls.Should().NotContain(c => c.Display.Contains("--vacuum-time", StringComparison.Ordinal));
        File.Exists(RunHistory.File(home.Paths)).Should().BeFalse("neither recorded anything");
    }

    // ---------- E3.S2: the deletions over the CAPTURED Docker, root claimed in the sandbox ----------

    /// <summary>A sandbox with the captured Docker answers, the journal's size, a target user <c>me</c> (no tool installed for
    /// them) and the age limits at 0 — the button's "everything unused".</summary>
    private static ScenarioHome Docker(string purpose)
    {
        var home = Journal(purpose, claimsRoot: true);
        foreach (var (command, file) in DockerFixture.Answers)
        {
            home.Script(DockerCommands.Executable, command.Arguments, 0, $"docker/{DockerFixture.Name}/{file}");
        }

        if (home.Paths.Side == HostSide.Wsl)
        {
            Write(home.Paths.DistroPath("/etc/passwd"), "root:x:0:0::/root:/bin/bash\nme:x:1000:1000::/home/me:/bin/bash\n");
        }

        Write(home.Paths.UserConfigFile, """{ "volumes": { "anonymousOlderThanDays": 0 }, "containers": { "stoppedOlderThanDays": 0 }, "images": { "unusedOlderThanDays": 0 }, "buildCache": { "olderThanDays": 0 } }""");
        return home;
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public async Task A_preview_of_every_action_this_build_holds_answers_each_from_live_state_and_starts_only_read_commands()
    {
        using var home = Docker("act-preview-all");
        var ids = string.Join(',', ActionRegistry.Product.Actions.Select(a => a.Id.Text));

        var result = await home.RunAsync("act", ids, "--preview", "--json");

        if (home.Paths.Side == HostSide.Windows)
        {
            result.Exit.Should().Be((int)ExitCode.Usage, "the distro's actions on the Windows binary name the side");
            return;
        }

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        var report = Report(result);
        report.Actions.Select(a => a.Id).Should().BeEquivalentTo(ActionRegistry.Product.Actions.Select(a => a.Id.Text));
        report.Actions.Single(a => a.Id == "A4").Preview!.Count.Should().Be(3, "the captured Docker's three unattached anonymous volumes");
        report.Actions.Single(a => a.Id == "A8").Reason.Should().Contain("npm is not installed for me", "a tool that is not installed is a skip with the reason");
        home.Calls.Where(c => c.Tool == DockerCommands.Executable).Should().OnlyContain(c => DockerCommands.IsReadVerb(c.Argv), "a preview removes nothing");
        Directory.Exists(home.Paths.StateDirectory).Should().BeFalse("a preview writes no state");
    }

    [Fact]
    public async Task A_button_run_of_a4_removes_only_the_volume_the_panel_showed_records_the_manual_trigger_and_freed_from_the_confirmed_one()
    {
        using var home = Docker("act-a4-manual");
        Assert.SkipWhen(home.Paths.Side == HostSide.Windows, "A4 runs inside the distro: covered on the Linux legs (and by hand in WSL)");
        var shown = DockerFixture.Read("volume-ls-dangling.out").Split('\n').Select(l => l.Trim()).First(DockerJson.IsFullId);
        home.Answer(new FakeAnswer(DockerCommands.Executable, ["volume", "rm", shown], 0, home.WriteFile("rm.out", shown + "\n"), string.Empty));

        var result = await home.RunAsync("act", "A4", "--confirm", "--manual", "--volume", shown, "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        var a4 = Report(result).Actions.Single();
        a4.Status.Should().Be(ActionStatus.Ran, a4.Reason);
        a4.Run!.Removed.Should().ContainSingle().Which.Key.Should().BeEmpty("the key is never serialised");
        a4.Run.FreedBytes.Should().Be(DockerFixture.Inventory.Volumes.Single(v => v.Name == shown).SizeBytes.ValueOr(-1));
        home.Calls.Where(c => c.Argv.Take(2).SequenceEqual(["volume", "rm"])).Should().ContainSingle().Which.Argv.Should().Equal("volume", "rm", shown);
        RunHistory.Read(home.Paths, new PhysicalFileSystem(home.Paths)).Records.Single().Trigger.Should().Be(RunTrigger.Manual);
    }
}
