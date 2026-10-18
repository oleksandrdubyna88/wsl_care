using FluentAssertions;

using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Worktrees;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Worktrees;

/// <summary>
/// E19.S3 Fable security review, condition 1: a root is judged under its REAL name. <c>C:\Users\SHORT~1</c> passed every rule while the profile
/// rule looked at the long name, and <c>D:\worktrees</c> beside <c>D:\WORKTR~1</c> counted as two roots. The folder is held and its final path
/// must equal the spelling; otherwise it is refused as <c>spelt-differently</c> naming its real name. Windows legs over real folders under a
/// <c>testsToDelete-</c> folder; an 8.3-free volume skips with that reason.
/// </summary>
public sealed class WorktreeRootsSpellingTests : IDisposable
{
    private const string LongName = "a-rather-long-folder-name-for-eight-three";

    private readonly ToDeleteRoot _root = new("roots-spelling");

    public void Dispose() => _root.Dispose();

    private (WindowsHostPaths Paths, PhysicalFileSystem Files) World(string profile)
    {
        var env = new WindowsEnvironment(profile, _root.Dir(@"profile-parts/roaming"), _root.Dir(@"profile-parts/local"), _root.Dir("programdata"), _root.Dir("temp"), _root.Path + @"\");
        var paths = new WindowsHostPaths(env);
        return (paths, new PhysicalFileSystem(paths) { OwnersAreThisProcess = true });
    }

    private static string ShortOrSkip(string folder)
    {
        var shortPath = ShortPaths.Of(folder);
        Assert.SkipWhen(string.Equals(shortPath, folder, StringComparison.OrdinalIgnoreCase), "8.3 names are not generated on this volume");
        return shortPath;
    }

    private static IReadOnlyList<RootVerdict> Judge((WindowsHostPaths Paths, PhysicalFileSystem Files) world, params string[] given) =>
        WorktreeRoots.Judge(world.Paths, world.Files, WorktreeRoots.ThisMachinesDrive, 8, given);

    [Fact]
    public void A_short_name_spelling_of_the_profile_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        var profile = _root.Dir(LongName);
        var shortProfile = ShortOrSkip(profile);

        var verdict = Judge(World(profile), shortProfile).Single();

        verdict.Accepted.Should().BeFalse("the short name IS the profile");
        verdict.Rule.Should().Be(RootRule.SpeltDifferently, verdict.Reason);
        verdict.Reason.Should().Contain("spelt differently from its real name").And.Contain(LongName);
    }

    [Fact]
    public void Two_spellings_of_one_folder_are_one_root()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        var folder = _root.Dir("work/" + LongName);
        var shortFolder = ShortOrSkip(folder);

        var verdicts = Judge(World(_root.Dir("profile-x")), folder, shortFolder);

        verdicts.Select(v => v.Accepted).Should().Equal(true, false);
        verdicts[1].Rule.Should().Be(RootRule.SpeltDifferently, verdicts[1].Reason);
    }

    [Fact]
    public void A_trailing_dot_says_it_is_spelt_differently_not_that_it_is_a_link()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        var folder = _root.Dir("work/plain");

        var verdict = Judge(World(_root.Dir("profile-y")), folder + ".").Single();

        verdict.Accepted.Should().BeFalse();
        verdict.Rule.Should().Be(RootRule.SpeltDifferently, verdict.Reason);
        verdict.Reason.Should().NotContain("link");
    }

    [Fact]
    public void A_folder_spelt_as_its_real_name_with_other_case_is_accepted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        var folder = _root.Dir("work/MixedCaseName");

        var verdict = Judge(World(_root.Dir("profile-z")), folder.ToUpperInvariant()).Single();

        verdict.Accepted.Should().BeTrue(verdict.Reason);
    }
}
