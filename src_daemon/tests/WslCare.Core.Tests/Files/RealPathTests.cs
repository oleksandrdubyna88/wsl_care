using FluentAssertions;

using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Tests.Files;

/// <summary>
/// The walk that turns a spelled path into the path the kernel would act on. The disk is a
/// dictionary here: link path → target.
/// </summary>
public sealed class RealPathTests
{
    private static Func<string, string?> Links(PathRules rules, params (string Link, string Target)[] links)
    {
        var table = links.ToDictionary(l => rules.Normalize(l.Link), l => l.Target, StringComparer.FromComparison(rules.Comparison));
        return path => table.GetValueOrDefault(rules.Normalize(path));
    }

    [Fact]
    public void A_path_without_links_is_normalised_and_dot_dot_is_applied()
    {
        RealPath.Resolve("/home/me/./work/../git/repo", PathRules.Linux, Links(PathRules.Linux))
            .Should().Be("/home/me/git/repo");
    }

    [Fact]
    public void Dot_dot_after_a_link_climbs_from_the_link_target_not_from_the_link()
    {
        // ~/cleanup/link -> ~/.claude/projects; "link/../x" is therefore ~/.claude/x, inside the agent folder.
        var links = Links(PathRules.Linux, ("/home/me/cleanup/link", "/home/me/.claude/projects"));

        RealPath.Resolve("/home/me/cleanup/link/../x", PathRules.Linux, links).Should().Be("/home/me/.claude/x");
    }

    [Fact]
    public void A_link_in_the_middle_of_the_path_is_followed_and_the_rest_appended()
    {
        var links = Links(PathRules.Linux, ("/home/me/cleanup/sessions", "/home/me/.claude/projects"));

        RealPath.Resolve("/home/me/cleanup/sessions/p1/s.jsonl", PathRules.Linux, links)
            .Should().Be("/home/me/.claude/projects/p1/s.jsonl");
    }

    [Fact]
    public void A_relative_link_target_is_resolved_against_the_link_parent()
    {
        var links = Links(PathRules.Linux, ("/home/me/a/link", "../b/target"));

        RealPath.Resolve("/home/me/a/link/file", PathRules.Linux, links).Should().Be("/home/me/b/target/file");
    }

    [Fact]
    public void A_chain_of_links_is_followed_to_the_end()
    {
        var links = Links(PathRules.Linux, ("/l1", "/l2"), ("/l2", "/l3"), ("/l3", "/real"));

        RealPath.Resolve("/l1/x", PathRules.Linux, links).Should().Be("/real/x");
    }

    [Fact]
    public void A_cycle_of_links_is_an_error_not_a_hang()
    {
        var links = Links(PathRules.Linux, ("/a", "/b"), ("/b", "/a"));

        var act = () => RealPath.Resolve("/a/x", PathRules.Linux, links);

        act.Should().Throw<IOException>().WithMessage("*too many*");
    }

    [Fact]
    public void Windows_junction_to_another_drive_changes_the_root_prefix()
    {
        var links = Links(PathRules.Windows, (@"C:\Users\me\AppData\Local\Temp\junc", @"D:\data\claude"));

        RealPath.Resolve(@"C:\Users\me\AppData\Local\Temp\junc\projects\x", PathRules.Windows, links)
            .Should().Be(@"D:\data\claude\projects\x");
    }

    [Fact]
    public void Windows_links_are_matched_case_insensitively()
    {
        var links = Links(PathRules.Windows, (@"C:\Users\me\link", @"C:\Users\me\.claude"));

        RealPath.Resolve(@"c:\users\ME\LINK\x", PathRules.Windows, links).Should().Be(@"C:\Users\me\.claude\x");
    }

    [Fact]
    public void Dot_dot_never_climbs_above_the_root()
    {
        RealPath.Resolve("/../../etc", PathRules.Linux, Links(PathRules.Linux)).Should().Be("/etc");
        RealPath.Resolve(@"C:\..\Windows", PathRules.Windows, Links(PathRules.Windows)).Should().Be(@"C:\Windows");
    }
}
