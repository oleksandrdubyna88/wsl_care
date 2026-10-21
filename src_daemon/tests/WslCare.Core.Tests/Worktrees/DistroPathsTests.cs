using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Health;
using WslCare.Core.Worktrees;

namespace WslCare.Core.Tests.Worktrees;

/// <summary>
/// E19.S3 (PLAN_disk_hygiene_windows.md §3.3, E19 stories §3 decision 8): the inverse of <see cref="HealthCollector.InDistro"/> — a
/// pointer a WSL-made worktree wrote (<c>/mnt/d/work/x</c>) read as the Windows folder it is (<c>D:\work\x</c>), through the automount
/// roots the configuration lists and nothing else. A POSIX path under none of them is not translated: the caller reads it as foreign.
/// </summary>
public sealed class DistroPathsTests
{
    private static readonly string[] Mnt = ["/mnt/"];

    private static string? Translated(string posix, params string[] roots) =>
        DistroPaths.ToWindows(posix, roots.Length == 0 ? Mnt : roots) is Reading<string>.Available { Value: var windows } ? windows : null;

    [Theory]
    [InlineData("/mnt/d/work/x/.git", @"D:\work\x\.git")]
    [InlineData("/mnt/c/Users/me/repo", @"C:\Users\me\repo")]
    [InlineData("/mnt/c", @"C:\")]
    [InlineData("/mnt/c/", @"C:\")]
    [InlineData("/mnt/z/a b/c", @"Z:\a b\c")]
    public void A_path_under_the_automount_root_is_the_drive_it_names(string posix, string expected)
    {
        Translated(posix).Should().Be(expected);
    }

    [Fact]
    public void A_listed_automount_root_other_than_mnt_translates_too()
    {
        Translated("/windrives/e/repos/x", "/mnt/", "/windrives/").Should().Be(@"E:\repos\x");
        Translated("/windrives/e/repos/x").Should().BeNull("a root nobody listed is not an automount root");
    }

    [Theory]
    [InlineData("/home/me/repo/.git")]
    [InlineData("/mnt")]
    [InlineData("/mnt/")]
    [InlineData("/mnt/cc/x")]
    [InlineData("/mnt/1/x")]
    [InlineData("/mnt/D/x")]
    [InlineData("/MNT/d/x")]
    [InlineData("/mnt/d/../x")]
    [InlineData("/mnt/d/a/../../x")]
    [InlineData("/mnt/d/x\ny")]
    [InlineData("/mnt/d/x\u0007")]
    [InlineData("mnt/d/x")]
    [InlineData(@"D:\work\x")]
    [InlineData("")]
    public void A_path_the_translation_cannot_map_is_not_translated(string posix)
    {
        Translated(posix).Should().BeNull($"{posix} is not an automounted Windows drive path");
    }

    [Fact]
    public void An_untranslatable_path_says_why()
    {
        DistroPaths.ToWindows("/home/me/repo", Mnt).Should().BeOfType<Reading<string>.Unavailable>().Which.Reason.Should().Contain("/home/me/repo");
    }

    [Fact]
    public void The_translation_is_the_inverse_of_the_forward_one()
    {
        foreach (var windows in new[] { @"D:\work\x", @"C:\Users\me\repo", @"Z:\a b" })
        {
            var posix = ((Reading<string>.Available)HealthCollector.InDistro(windows, "/mnt/")).Value;

            Translated(posix).Should().Be(windows);
        }
    }
}
