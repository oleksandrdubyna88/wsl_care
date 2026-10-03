using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.PackageCaches;
using WslCare.Core.Config;
using WslCare.Core.Health;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions;

/// <summary>A9 (E3.S2): <c>apt-get clean</c> and <c>snap remove --revision</c> of the revisions that are STILL disabled at run
/// time, measured from the apt cache walked before / after and the snap files gone after; a tool that is not installed
/// skips its part.</summary>
public sealed class PackageCacheTests : IDisposable
{
    private const string SnapListing = """
        Name    Version   Rev    Tracking       Publisher   Notes
        core22  20240111  1122   latest/stable  canonical✓  base,disabled
        core22  20240408  1380   latest/stable  canonical✓  base
        lxd     5.21.1    28460  5.21/stable    canonical✓  disabled
        snapd   2.63      21759  latest/stable  canonical✓  snapd
        evil    1.0       x1     -              me          disabled
        """;

    private readonly LinuxSandbox _sandbox = new("a9");

    public void Dispose() => _sandbox.Dispose();

    private (ActionContext Context, ActionCommands Commands, RecordingCommandRunner Runner) For(RecordingCommandRunner runner)
    {
        var context = new ActionContext(_sandbox.Paths, _sandbox.Files, new FixedTimeProvider(), ConfigLoader.Load(_sandbox.Paths, _sandbox.Files).Config, RunTrigger.Cli, new TargetUserResult.None("machine-scoped"));
        var action = new PackageCacheClean();
        return (context, new ActionCommands(action, runner, context.TargetUser, []), runner);
    }

    [Fact]
    public async Task A9_cleans_apt_and_removes_only_revisions_still_disabled_measuring_each_from_its_file()
    {
        var archive = _sandbox.Sized("/var/cache/apt/archives/x.deb", 9000, FixedTimeProvider.DefaultNow);
        var core = _sandbox.Sized("/var/lib/snapd/snaps/core22_1122.snap", 70_000, FixedTimeProvider.DefaultNow);
        var lxd = _sandbox.Sized("/var/lib/snapd/snaps/lxd_28460.snap", 30_000, FixedTimeProvider.DefaultNow);
        var runner = new RecordingCommandRunner { Policy = CommandPolicy.Product, Default = new CommandOutcome.FailedToStart("not scripted") }
            .Script(HealthCommands.SnapList.Argv, 0, SnapListing)
            .ScriptEffect(argv => argv is ["apt-get", "clean"], _ => { File.Delete(archive); return RecordingCommandRunner.Exited(0); })
            .ScriptEffect(argv => argv is ["snap", "remove", "core22", "--revision=1122"], _ => { File.Delete(core); return RecordingCommandRunner.Exited(0); })
            .ScriptEffect(argv => argv is ["snap", "remove", "lxd", "--revision=28460"], _ => { File.Delete(lxd); return RecordingCommandRunner.Exited(0); });
        var (context, commands, _) = For(runner);
        var action = new PackageCacheClean();

        var run = await action.RunAsync(context, await action.PreviewAsync(context, commands, CancellationToken.None), commands, CancellationToken.None);

        run.Succeeded.Should().BeTrue(run.Failure);
        runner.Commands.Where(c => c.StartsWith("snap remove", StringComparison.Ordinal)).Should().Equal("snap remove core22 --revision=1122", "snap remove lxd --revision=28460");
        run.FreedBytes.Should().Be(9000 + 70_000 + 30_000);
        run.Notes.Should().Contain(n => n.Contains("evil", StringComparison.Ordinal) && n.Contains("kept", StringComparison.Ordinal), "an x-revision is not a numeric revision this action removes");
        runner.Requests.Should().OnlyContain(r => CommandPolicy.Product.Review(r).IsAllowed);
    }

    [Fact]
    public async Task A9_without_snap_or_apt_get_skips_those_parts_and_is_not_a_failure()
    {
        var (context, commands, runner) = For(new RecordingCommandRunner { Policy = CommandPolicy.Product, Default = new CommandOutcome.FailedToStart("not installed") });
        var action = new PackageCacheClean();

        var run = await action.RunAsync(context, await action.PreviewAsync(context, commands, CancellationToken.None), commands, CancellationToken.None);

        run.Succeeded.Should().BeTrue(run.Failure);
        run.Notes.Should().Contain(n => n.StartsWith("apt-get clean skipped", StringComparison.Ordinal)).And.Contain(n => n.StartsWith("snap revisions skipped", StringComparison.Ordinal));
        runner.Commands.Should().NotContain(c => c.StartsWith("snap remove", StringComparison.Ordinal));
    }

    [Fact]
    public void A9_triggers_on_an_apt_cache_above_200_mib_or_any_disabled_revision()
    {
        var action = new PackageCacheClean();
        ActionPreview Preview(long apt, long revisions) =>
            ActionPreview.Of("a9", 0, 0, string.Empty, new Dictionary<string, long> { [PackageCacheClean.AptBytesFact] = apt, [PackageCacheClean.DisabledRevisionsFact] = revisions }, string.Empty, []);
        var config = ConfigLoader.Load(_sandbox.Paths, _sandbox.Files).Config;

        action.Trigger(Preview(PackageCacheClean.AptTriggerBytes, 0), config).Fired.Should().BeFalse();
        action.Trigger(Preview(PackageCacheClean.AptTriggerBytes + 1, 0), config).Fired.Should().BeTrue();
        action.Trigger(Preview(0, 1), config).Fired.Should().BeTrue();
    }
}
