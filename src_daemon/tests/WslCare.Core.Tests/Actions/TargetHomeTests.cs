using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Config;
using WslCare.Core.Folders;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions;

/// <summary>
/// Plan §15c #2, closed in E3.S2: as ROOT, the daily folder walk, the cache roots and the user configuration layer are the
/// TARGET user's home, never root's <c>$HOME</c>; with an ambiguous target the user layer is left out, machine-scoped actions
/// still run and user-scoped ones refuse (gate finding #2); unprivileged, <c>$HOME</c> already is the user's.
/// </summary>
public sealed class TargetHomeTests : IDisposable
{
    private const string Accounts = "root:x:0:0::/root:/bin/bash\nalice:x:1000:1000::/home/alice:/bin/bash\nsam:x:1001:1001::/home/sam:/bin/bash\n";

    private readonly LinuxSandbox _sandbox = new("target-home");

    public void Dispose() => _sandbox.Dispose();

    [Fact]
    public void As_root_the_folder_walk_and_the_user_layer_follow_the_target_users_home_and_roots_home_stays_protected()
    {
        _sandbox.Write("/etc/passwd", Accounts);
        _sandbox.Write("/etc/wsl.conf", "[user]\ndefault=alice\n");

        var (paths, owner) = TargetHome.Resolve(_sandbox.Paths, _sandbox.Files, privileged: true);

        owner.Should().BeOfType<HomeOwner.Target>().Which.User.Name.Should().Be("alice");
        paths.Home.Should().Be(_sandbox.Paths.DistroPath("/home/alice"));
        paths.UserConfigFile.Should().StartWith(_sandbox.Paths.DistroPath("/home/alice")).And.EndWith("config.json");
        FolderSizes.Targets(paths).Single(t => t.Id == FolderSizes.NpmCache).Path.Should().StartWith(_sandbox.Paths.DistroPath("/home/alice"));
        paths.GitRoots.Should().Contain(r => r.StartsWith(_sandbox.Paths.Home, StringComparison.Ordinal), "the home it replaced is still protected");
    }

    [Fact]
    public async Task As_root_with_an_ambiguous_target_machine_scoped_actions_still_run_and_only_user_scoped_ones_refuse()
    {
        // Gate finding #2 (plan 15c #2): ambiguity makes every USER-scoped action refuse with the reason; machine-scoped actions
        // still run. The run used to go observe-only as a whole, so an ambiguous account list stopped A1 / A4 / A10 too.
        _sandbox.Write("/etc/passwd", Accounts);
        _sandbox.Write("/home/alice/.config/wsl-care/config.json", "{ not json");
        var journal = new List<string>();

        var (paths, owner) = TargetHome.Resolve(_sandbox.Paths, _sandbox.Files, privileged: true);
        var loaded = ConfigLoader.Load(paths, _sandbox.Files, UserLayerTrusts.For(owner, static () => string.Empty));
        var engine = new ActionEngine(new EngineContext(paths, _sandbox.Files, new RecordingCommandRunner(), new FixedTimeProvider(), new FakeProbe(paths.Side, new FixedTimeProvider()), loaded,
            new FakeProcessTable().Alive(77, FixedTimeProvider.DefaultNow.AddMinutes(-1)), 77, new ActionRegistry([new ScriptedAction("A10", journal), new ScriptedAction("A8", journal) { Scope = CommandScope.User }])));
        var result = await engine.ExecuteAsync(new ActRequest([ActionId.Find("A10")!, ActionId.Find("A8")!], RunTrigger.Cli, Execute: true), CancellationToken.None);

        owner.Should().BeOfType<HomeOwner.Unknown>();
        loaded.IsObserveOnly.Should().BeFalse("without a single target user the user layer is left out, not made an error that stops every action");
        var actions = result.Should().BeOfType<ActResult.Done>().Subject.Detail.Actions;
        actions.Single(a => a.Id == "A10").Status.Should().Be(ActionStatus.Ran, "a machine-scoped action needs no target user");
        actions.Single(a => a.Id == "A8").Should().Match<ActionOutcome>(a => a.Status == ActionStatus.Refused && a.Reason.Contains("alice") && a.Reason.Contains("sam"));
    }

    [Fact]
    public void Unprivileged_the_paths_are_this_processs_own_whatever_wsl_conf_says()
    {
        _sandbox.Write("/etc/passwd", Accounts);
        _sandbox.Write("/etc/wsl.conf", "[user]\ndefault=alice\n");

        var (paths, owner) = TargetHome.Resolve(_sandbox.Paths, _sandbox.Files, privileged: false);

        owner.Should().BeOfType<HomeOwner.ThisProcess>();
        paths.Should().BeSameAs(_sandbox.Paths);
        ConfigLoader.Load(paths, _sandbox.Files, UserLayerTrusts.For(owner, static () => string.Empty)).IsObserveOnly.Should().BeFalse();
    }
}
