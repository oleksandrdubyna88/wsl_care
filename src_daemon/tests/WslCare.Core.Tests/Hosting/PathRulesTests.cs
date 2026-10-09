using FluentAssertions;

using WslCare.Core.Hosting;

namespace WslCare.Core.Tests.Hosting;

/// <summary>
/// The one comparison every protective rule rests on, on both path families, on whichever OS runs
/// the suite.
/// </summary>
public sealed class PathRulesTests
{
    [Theory]
    [InlineData("/home/me/git/repo", "/home/me/git", true)]
    [InlineData("/home/me/git", "/home/me/git", false)]
    [InlineData("/home/me/gitx/repo", "/home/me/git", false)]
    [InlineData("/home/me/Git/repo", "/home/me/git", false)]
    [InlineData("/home/me/git/", "/home/me/git", false)]
    [InlineData("/etc/x", "/", true)]
    public void Linux_strictly_under_is_a_proper_descendant_and_case_sensitive(string path, string root, bool expected)
    {
        PathRules.Linux.IsStrictlyUnder(path, root).Should().Be(expected);
    }

    [Theory]
    [InlineData(@"C:\Users\me\git\repo", @"C:\Users\me\git", true)]
    [InlineData(@"C:\Users\ME\GIT\repo", @"c:\users\me\git", true)]
    [InlineData(@"C:/Users/me/git/repo", @"C:\Users\me\git", true)]
    [InlineData(@"C:\Users\me\gitx\repo", @"C:\Users\me\git", false)]
    [InlineData(@"C:\Users\me\git", @"C:\Users\me\git\", false)]
    [InlineData(@"D:\Users\me\git\repo", @"C:\Users\me\git", false)]
    [InlineData(@"C:\Windows", @"C:\", true)]
    public void Windows_strictly_under_is_case_insensitive_and_accepts_both_separators(string path, string root, bool expected)
    {
        PathRules.Windows.IsStrictlyUnder(path, root).Should().Be(expected);
    }

    [Fact]
    public void Join_uses_the_family_separator_whatever_the_host_is()
    {
        PathRules.Linux.Join("/home/me", ".claude", "projects").Should().Be("/home/me/.claude/projects");
        PathRules.Windows.Join(@"C:\Users\me", ".claude", "projects").Should().Be(@"C:\Users\me\.claude\projects");
        PathRules.Windows.Join(@"C:\", "ProgramData").Should().Be(@"C:\ProgramData");
    }

    [Fact]
    public void Normalize_trims_trailing_separators_but_keeps_a_bare_root()
    {
        PathRules.Linux.Normalize("/home/me//git/").Should().Be("/home/me/git");
        PathRules.Linux.Normalize("/").Should().Be("/");
        PathRules.Windows.Normalize(@"C:/Users\me\").Should().Be(@"C:\Users\me");
        PathRules.Windows.Normalize(@"C:\").Should().Be(@"C:\");
        PathRules.Windows.Normalize(@"\\server\share\dir\").Should().Be(@"\\server\share\dir");
    }

    [Fact]
    public void Split_separates_the_root_prefix_from_the_segments()
    {
        PathRules.Linux.Split("/home/me/x").Prefix.Should().Be("/");
        PathRules.Linux.Split("/home/me/x").Segments.Should().Equal("home", "me", "x");
        PathRules.Windows.Split(@"C:\Users\me").Prefix.Should().Be(@"C:\");
        PathRules.Windows.Split(@"C:\Users\me").Segments.Should().Equal("Users", "me");
        PathRules.Windows.Split(@"\\server\share\dir\file").Prefix.Should().Be(@"\\server\share\");
        PathRules.Windows.Split(@"\\server\share\dir\file").Segments.Should().Equal("dir", "file");
    }

    [Fact]
    public void A_relative_path_is_refused_by_split_because_the_policy_never_judges_one()
    {
        var act = () => PathRules.Linux.Split("home/me");

        act.Should().Throw<ArgumentException>().WithMessage("*not an absolute path*");
        PathRules.Windows.IsAbsolute("C:relative").Should().BeFalse("a drive-relative path is not absolute");
    }

    [Fact]
    public void IsRoot_recognises_the_filesystem_roots_of_each_family()
    {
        PathRules.Linux.IsRoot("/").Should().BeTrue();
        PathRules.Linux.IsRoot("/home").Should().BeFalse();
        PathRules.Windows.IsRoot(@"C:\").Should().BeTrue();
        PathRules.Windows.IsRoot(@"\\server\share").Should().BeTrue();
        PathRules.Windows.IsRoot(@"C:\Users").Should().BeFalse();
    }

    /// <summary>E9.S0 review round: a Windows path's folder taken inside the distribution, where <c>Path.GetDirectoryName</c> reads
    /// it as one name and answers empty.</summary>
    [Fact]
    public void Parent_is_the_folder_by_each_familys_own_rules_on_any_host()
    {
        PathRules.Windows.Parent(@"C:\Users\me\AppData\Roaming\wsl-care\config.json").Should().Be(@"C:\Users\me\AppData\Roaming\wsl-care");
        PathRules.Windows.Parent(@"C:\").Should().Be(@"C:\");
        PathRules.Linux.Parent("/home/me/.config/wsl-care/config.json").Should().Be("/home/me/.config/wsl-care");
        PathRules.Linux.Parent("/").Should().Be("/");
    }
}
