using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Worktrees;

namespace WslCare.Core.Tests.Worktrees;

/// <summary>
/// E19.S3 (PLAN_disk_hygiene_windows.md §3.3): what a <c>.git</c> FILE says — <c>gitdir: &lt;path&gt;</c>, one line, as git writes it — read
/// as text, never by starting git. Anything else is not a pointer.
/// </summary>
public sealed class GitPointersTests
{
    private static string? Path(string text) => GitPointers.Parse(text) is Reading<string>.Available { Value: var path } ? path : null;

    [Theory]
    [InlineData("gitdir: C:/repo/.git/worktrees/x\n", "C:/repo/.git/worktrees/x")]
    [InlineData("gitdir: C:/repo/.git/worktrees/x", "C:/repo/.git/worktrees/x")]
    [InlineData("gitdir: C:/repo/.git/worktrees/x\r\n", "C:/repo/.git/worktrees/x")]
    [InlineData(@"gitdir: C:\repo\.git\worktrees\x", @"C:\repo\.git\worktrees\x")]
    [InlineData("gitdir: /mnt/c/repo/.git/worktrees/x\n", "/mnt/c/repo/.git/worktrees/x")]
    [InlineData("gitdir: ../main/.git/worktrees/x\n", "../main/.git/worktrees/x")]
    [InlineData("gitdir: C:/a b/c\n", "C:/a b/c")]
    public void A_gitdir_line_names_its_path(string text, string expected)
    {
        Path(text).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("hello")]
    [InlineData("gitdir:")]
    [InlineData("gitdir: \n")]
    [InlineData("gitdir:C:/repo")]
    [InlineData("GITDIR: C:/repo")]
    [InlineData("gitdir: C:/a\nsomething else\n")]
    [InlineData("gitdir: C:/a\u0007b\n")]
    [InlineData("\u00ef\u00bb\u00bfgitdir: C:/a")]
    public void Anything_else_is_not_a_pointer_and_says_why(string text)
    {
        GitPointers.Parse(text).Should().BeOfType<Reading<string>.Unavailable>().Which.Reason.Should().NotBeEmpty();
    }
}
