using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Config;
using WslCare.Core.Folders;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions;

/// <summary>
/// Plan §15c #2, closed in E3.S2: as ROOT, the daily folder walk, the cache roots and the user configuration layer are the
/// TARGET user's home, never root's <c>$HOME</c>; with an ambiguous target the user layer cannot be located and the run is
/// observe-only; unprivileged, <c>$HOME</c> already is the user's.
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
    public void As_root_with_an_ambiguous_target_the_user_layer_cannot_be_located_and_the_run_is_observe_only()
    {
        _sandbox.Write("/etc/passwd", Accounts);

        var (paths, owner) = TargetHome.Resolve(_sandbox.Paths, _sandbox.Files, privileged: true);
        var loaded = ConfigLoader.Load(paths, _sandbox.Files, owner.UserLayerProblem);

        owner.Should().BeOfType<HomeOwner.Unknown>();
        loaded.IsObserveOnly.Should().BeTrue("a default must not stand in for a setting the user may have changed");
        loaded.Errors.Single().Display.Should().Contain("no single target user");
    }

    [Fact]
    public void Unprivileged_the_paths_are_this_processs_own_whatever_wsl_conf_says()
    {
        _sandbox.Write("/etc/passwd", Accounts);
        _sandbox.Write("/etc/wsl.conf", "[user]\ndefault=alice\n");

        var (paths, owner) = TargetHome.Resolve(_sandbox.Paths, _sandbox.Files, privileged: false);

        owner.Should().BeOfType<HomeOwner.ThisProcess>();
        paths.Should().BeSameAs(_sandbox.Paths);
        ConfigLoader.Load(paths, _sandbox.Files, owner.UserLayerProblem).IsObserveOnly.Should().BeFalse();
    }
}
