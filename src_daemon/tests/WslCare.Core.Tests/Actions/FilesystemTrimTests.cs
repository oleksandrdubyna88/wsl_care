using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Disk;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions;

/// <summary>
/// A15 (E3.S3): <c>fstrim -av</c> WEEKLY — the history's last A15 that ran — and only when <c>/</c> has no <c>discard</c> and
/// <c>fstrim.timer</c> is not enabled; fstrim's own per-filesystem report as the measured result; exit 64 a success with a
/// note. fstrim and systemctl are a recording runner under the PRODUCT policy.
/// </summary>
public sealed class FilesystemTrimTests : IDisposable
{
    private static readonly DateTimeOffset Now = FixedTimeProvider.DefaultNow;

    private readonly LinuxSandbox _sandbox = new("a15");
    private readonly FilesystemTrim _action = new();
    private readonly RecordingCommandRunner _runner = new() { Policy = CommandPolicy.Product };

    public void Dispose() => _sandbox.Dispose();

    private void Mounts(bool discard) =>
        _sandbox.Write("/proc/mounts", $"/dev/sdc / ext4 rw,relatime{(discard ? ",discard" : string.Empty)} 0 0\nC:\\134 /mnt/c 9p rw 0 0\n");

    private void Timer(string state) =>
        _runner.Script(Systemd.SystemdCommands.ShowUnit("fstrim.timer").Argv, RecordingCommandRunner.Exited(0, $"Id=fstrim.timer\nLoadState=loaded\nActiveState=inactive\nSubState=dead\nUnitFileState={state}\n"));

    private void TrimmedAt(DateTimeOffset at) =>
        new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(new RunRecord(1, RunId.New(at, 7), RunTrigger.Timer, at, at, RunOutcome.Completed, [new ActionRecord("A15", 1, 0) { Status = ActionStatus.Ran }]));

    private (ActionContext Context, ActionCommands Commands) For()
    {
        var config = ConfigLoader.Load(_sandbox.Paths, _sandbox.Files).Config;
        var context = new ActionContext(_sandbox.Paths, _sandbox.Files, new FixedTimeProvider(Now), config, RunTrigger.Timer, new TargetUserResult.None("not needed"));
        return (context, new ActionCommands(_action, _runner, context.TargetUser, []));
    }

    private async Task<TriggerDecision> TriggerAsync()
    {
        var (context, commands) = For();
        return _action.Trigger(await _action.PreviewAsync(context, commands, CancellationToken.None), context.Config);
    }

    [Fact]
    public async Task Weekly_a_trim_is_due_when_none_ran_or_the_last_ran_seven_days_ago_and_not_before()
    {
        Mounts(discard: false);
        Timer("disabled");
        (await TriggerAsync()).Should().Match<TriggerDecision>(d => d.Fired && d.Reason.Contains("never run"));

        TrimmedAt(Now.AddDays(-6.9));
        (await TriggerAsync()).Should().Match<TriggerDecision>(d => !d.Fired && d.Reason.Contains("6 day(s) ago"));

        TrimmedAt(Now.AddDays(-7));
        (await TriggerAsync()).Should().Match<TriggerDecision>(d => !d.Fired, "the NEWEST trim counts: 6.9 days ago is newer than 7");
    }

    [Fact]
    public async Task A_week_after_the_last_trim_it_fires()
    {
        Mounts(discard: false);
        Timer("disabled");
        TrimmedAt(Now.AddDays(-7));

        (await TriggerAsync()).Should().Match<TriggerDecision>(d => d.Fired && d.Reason.Contains("7 day(s) ago"));
    }

    [Fact]
    public async Task Discard_on_root_or_an_enabled_fstrim_timer_or_an_unread_timer_keeps_the_timer_away()
    {
        Mounts(discard: true);
        Timer("disabled");
        (await TriggerAsync()).Reason.Should().Contain("mounted with discard");

        Mounts(discard: false);
        Timer("enabled");
        (await TriggerAsync()).Reason.Should().Contain("fstrim.timer is enabled");
    }

    [Fact]
    public async Task An_unread_fstrim_timer_does_not_fire_and_an_unread_mount_table_is_unavailable()
    {
        Mounts(discard: false);
        _runner.Script(Systemd.SystemdCommands.ShowUnit("fstrim.timer").Argv, new CommandOutcome.FailedToStart("no systemctl"));
        (await TriggerAsync()).Should().Match<TriggerDecision>(d => !d.Fired && d.Reason.Contains("not read"));

        File.Delete(_sandbox.Paths.DistroPath("/proc/mounts"));
        var (context, commands) = For();
        (await _action.PreviewAsync(context, commands, CancellationToken.None)).Available.Should().BeFalse();
    }

    [Fact]
    public async Task The_run_records_each_filesystem_fstrim_reports_and_exit_64_is_a_success_with_a_note()
    {
        Mounts(discard: false);
        Timer("disabled");
        _runner.Script(["fstrim", "-av"], 64, "/: 12.3 GiB (13207024640 bytes) trimmed on /dev/sdc\n/mnt/wsl/data dir: 1 MiB (1048576 bytes) trimmed\n", "fstrim: /boot: FITRIM ioctl failed: Operation not supported");
        var (context, commands) = For();

        var run = await _action.RunAsync(context, await _action.PreviewAsync(context, commands, CancellationToken.None), commands, CancellationToken.None);

        run.Succeeded.Should().BeTrue();
        run.Removed.Select(r => (r.Name, r.Bytes, r.Note)).Should().Equal(("/", 13207024640L, "trimmed on /dev/sdc"), ("/mnt/wsl/data dir", 1048576L, "trimmed"));
        run.FreedBytes.Should().BeNull("trimmed blocks go back to the VHDX; nothing inside a filesystem is freed");
        run.Notes.Should().Contain(n => n.Contains("13208073216 bytes trimmed on 2")).And.Contain(n => n.Contains("exited 64") && n.Contains("FITRIM"));
    }

    [Fact]
    public async Task Any_other_failing_exit_is_a_failure()
    {
        Mounts(discard: false);
        _runner.Script(["fstrim", "-av"], 32, string.Empty, "fstrim: all filesystems failed");
        var (context, commands) = For();

        var run = await _action.RunAsync(context, ActionPreview.Unavailable("x", "y"), commands, CancellationToken.None);

        run.Succeeded.Should().BeFalse();
        run.Failure.Should().Contain("fstrim -av exited 32");
    }
}
