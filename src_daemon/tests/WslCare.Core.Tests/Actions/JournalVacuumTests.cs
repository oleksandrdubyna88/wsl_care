using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions;

/// <summary>
/// A10, the reference action (plan §5): its live preview (journald's own size; the archived files older than the limit), its
/// timer trigger (above 1 GiB), and its MEASURED result — the sizes, read before, of exactly the files that are gone after.
/// journalctl is a recording runner under the PRODUCT policy; the "vacuum" is the test deleting files, so nothing real runs.
/// </summary>
public sealed class JournalVacuumTests : IDisposable
{
    private const string Machine = "/var/log/journal/0123456789abcdef";
    private static readonly DateTimeOffset Now = FixedTimeProvider.DefaultNow;

    private readonly LinuxSandbox _sandbox = new("a10");
    private readonly JournalVacuum _action = new();
    private readonly string _oldSystem;
    private readonly string _oldUser;

    public JournalVacuumTests()
    {
        _sandbox.Sized($"{Machine}/system.journal", 4000, Now.AddDays(-90));
        _oldSystem = _sandbox.Sized($"{Machine}/system@0005f0-0000000000000001-0005f0a1b2c3d4e5.journal", 3000, Now.AddDays(-40));
        _oldUser = _sandbox.Sized($"{Machine}/user-1000@0005f1-0000000000000002-0005f0a1b2c3d4e6.journal", 2000, Now.AddDays(-31));
        _sandbox.Sized($"{Machine}/system@0005f2-0000000000000003-0005f0a1b2c3d4e7.journal", 1000, Now.AddDays(-5));
        _sandbox.Sized($"{Machine}/notes.txt", 500, Now.AddDays(-400));
        _sandbox.Sized("/run/log/journal/0123456789abcdef/system.journal", 700, Now.AddDays(-1));
    }

    public void Dispose() => _sandbox.Dispose();

    private static RecordingCommandRunner Journalctl(string diskUsage = "Archived and active journals take up 1.5G in the file system.") =>
        new RecordingCommandRunner { Policy = CommandPolicy.Product }.Script(SystemdCommandsDiskUsage, 0, diskUsage);

    private static IReadOnlyList<string> SystemdCommandsDiskUsage => Systemd.SystemdCommands.JournalDiskUsage.Argv;

    private (ActionContext Context, ActionCommands Commands) For(RecordingCommandRunner runner, string userConfig = "{}")
    {
        _sandbox.Write("/home/me/.config/wsl-care/config.json", userConfig);
        var config = ConfigLoader.Load(_sandbox.Paths, _sandbox.Files).Config;
        var context = new ActionContext(_sandbox.Paths, _sandbox.Files, new FixedTimeProvider(Now), config, RunTrigger.Cli, new TargetUserResult.None("not needed"));
        return (context, new ActionCommands(_action, runner, context.TargetUser, []));
    }

    [Fact]
    public async Task The_preview_counts_only_archived_files_older_than_the_limit_and_carries_journald_s_own_size()
    {
        var (context, commands) = For(Journalctl());

        var preview = await _action.PreviewAsync(context, commands, CancellationToken.None);

        preview.Available.Should().BeTrue(preview.Reason);
        preview.Count.Should().Be(2, "the active system.journal, the 5-day-old archive and the stray .txt are not what --vacuum-time=30d removes");
        preview.Bytes.Should().Be(5000);
        preview.Items.Select(i => Path.GetFileName(i.Name)).Should().BeEquivalentTo(Path.GetFileName(_oldSystem), Path.GetFileName(_oldUser));
        preview.Facts[JournalVacuum.JournalBytesFact].Should().Be((long)(1.5 * (1L << 30)));
        preview.What.Should().Contain("--vacuum-time=30d");
    }

    [Theory]
    [InlineData("Archived and active journals take up 1.1G in the file system.", true)]
    [InlineData("Archived and active journals take up 1.0G in the file system.", false)]
    [InlineData("Archived and active journals take up 900.0M in the file system.", false)]
    public async Task The_timer_trigger_is_the_journal_above_one_gibibyte(string diskUsage, bool fires)
    {
        var (context, commands) = For(Journalctl(diskUsage));

        var preview = await _action.PreviewAsync(context, commands, CancellationToken.None);

        _action.Trigger(preview, context.Config).Fired.Should().Be(fires);
    }

    [Fact]
    public async Task A_journal_whose_size_cannot_be_read_has_no_preview_and_the_reason_says_why()
    {
        var runner = new RecordingCommandRunner { Policy = CommandPolicy.Product }.Script(SystemdCommandsDiskUsage, new CommandOutcome.FailedToStart("no journalctl"));
        var (context, commands) = For(runner);

        var preview = await _action.PreviewAsync(context, commands, CancellationToken.None);

        preview.Available.Should().BeFalse();
        preview.Reason.Should().Contain("journalctl could not be started");
    }

    [Fact]
    public async Task The_freed_bytes_are_measured_from_the_files_that_are_gone_not_estimated_from_the_preview()
    {
        // The "vacuum" removes ONE of the two old archives (journald's own judgement may differ from the preview's estimate).
        var runner = Journalctl().ScriptEffect(argv => argv.Count == 2 && argv[1].StartsWith("--vacuum-time=", StringComparison.Ordinal), _ =>
        {
            File.Delete(_oldSystem);
            return RecordingCommandRunner.Exited(0, string.Empty, "Vacuuming done, freed 2.9K of archived journals from /var/log/journal/0123456789abcdef.");
        });
        var (context, commands) = For(runner, """{ "journal": { "keepDays": 30 } }""");
        var preview = await _action.PreviewAsync(context, commands, CancellationToken.None);

        var run = await _action.RunAsync(context, preview, commands, CancellationToken.None);

        run.Succeeded.Should().BeTrue(run.Failure);
        run.Count.Should().Be(1);
        run.FreedBytes.Should().Be(3000, "the size, read before, of the one file that is gone — not the preview's 5000");
        run.BeforeBytes.Should().Be(4000 + 3000 + 2000 + 1000 + 700);
        run.AfterBytes.Should().Be(4000 + 2000 + 1000 + 700);
        Path.GetFullPath(run.Removed.Should().ContainSingle().Subject.Name).Should().Be(Path.GetFullPath(_oldSystem));
        runner.Requests.Select(r => r.Display).Should().Equal(["journalctl --disk-usage", "journalctl --vacuum-time=30d"]);
        run.Commands.Select(c => c.Outcome).Should().Equal("exited", "exited");
    }

    [Fact]
    public async Task A_vacuum_that_fails_is_a_failure_with_journalctl_s_own_words_and_still_measured()
    {
        var runner = Journalctl().Script(argv => argv.Count == 2 && argv[1].StartsWith("--vacuum-time=", StringComparison.Ordinal), RecordingCommandRunner.Exited(1, string.Empty, "Failed to vacuum: Permission denied"));
        var (context, commands) = For(runner);
        var preview = await _action.PreviewAsync(context, commands, CancellationToken.None);

        var run = await _action.RunAsync(context, preview, commands, CancellationToken.None);

        run.Succeeded.Should().BeFalse();
        run.Failure.Should().Contain("exited 1").And.Contain("Permission denied");
        run.FreedBytes.Should().Be(0);
    }

    [Fact]
    public async Task Through_the_engine_a_button_press_runs_a10_under_the_product_policy_and_records_it()
    {
        _sandbox.Write("/etc/passwd", "root:x:0:0::/root:/bin/bash\n");
        var runner = Journalctl();
        var engine = new ActionEngine(new EngineContext(_sandbox.Paths, _sandbox.Files, runner, new FixedTimeProvider(Now), new FakeProbe(Core.Hosting.HostSide.Wsl, new FixedTimeProvider(Now)),
            ConfigLoader.Load(_sandbox.Paths, _sandbox.Files), new FakeProcessTable(), 77, ActionRegistry.Product));

        var result = await engine.ExecuteAsync(new ActRequest([_action.Id], RunTrigger.Cli, Execute: true), CancellationToken.None);

        var done = result.Should().BeOfType<ActResult.Done>().Subject;
        done.Detail.Actions.Single().Status.Should().Be(ActionStatus.Ran, done.Detail.Actions.Single().Reason);
        runner.Requests.Should().OnlyContain(r => CommandPolicy.Product.Review(r).IsAllowed);
        RunHistory.Read(_sandbox.Paths, _sandbox.Files).Records.Single().Actions.Single().Should().Match<ActionRecord>(a => a.Id == "A10" && a.Status == "ran");
    }
}
