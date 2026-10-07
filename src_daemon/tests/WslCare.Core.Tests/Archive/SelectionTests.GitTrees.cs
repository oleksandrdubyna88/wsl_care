using FluentAssertions;

using WslCare.Core.Archive;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Archive;

/// <summary>
/// Owner rule 2026-10-07 — the archive never selects anything inside a git working tree or a <c>.git</c> folder, an original clone and a
/// worktree alike (a worktree's <c>.git</c> is a FILE). Three places a repository can be: below a unit (a companion folder holding one),
/// around a unit (the project folder is a working tree), above the agent's whole folder (a home kept in git).
/// </summary>
public sealed partial class SelectionTests
{
    [Theory]
    [InlineData("/home/me/.claude/projects/p/s1/subagents/repo/.git/HEAD")]
    [InlineData("/home/me/.claude/projects/p/s1/subagents/repo/.git")]
    [InlineData("/home/me/.claude/projects/p/s1/subagents/.GIT/config")]
    public void A_session_with_a_git_folder_or_file_among_its_files_is_never_selected(string gitEntry)
    {
        File("/home/me/.claude/projects/p/s1.jsonl", DaysAgo(40));
        File("/home/me/.claude/projects/p/s1/subagents/repo/README.md", DaysAgo(40));
        File(gitEntry, DaysAgo(40));

        var claude = Claude();

        claude.Due.Should().BeEmpty("a repository below a session is never taken with it");
        claude.Skipped.Should().ContainSingle().Which.SkipRule.Should().Be(SkipRule.GitTree);
    }

    [Theory]
    [InlineData("/home/me/.claude/projects/p/.git/HEAD")]
    [InlineData("/home/me/.claude/projects/p/.git")]
    public void A_session_whose_folder_is_a_git_working_tree_is_never_selected_and_its_neighbours_are(string gitEntry)
    {
        File("/home/me/.claude/projects/p/s1.jsonl", DaysAgo(40));
        File("/home/me/.claude/projects/q/s2.jsonl", DaysAgo(40));
        File(gitEntry, DaysAgo(40));

        var claude = Claude();

        claude.Due.Select(u => u.Key).Should().Equal(["projects/q/s2.jsonl"], "only the project that is a working tree stays");
        claude.Skipped.Should().ContainSingle().Which.Should().Match<UnitFound>(u => u.Key == "projects/p/s1.jsonl" && u.SkipRule == SkipRule.GitTree);
    }

    [Theory]
    [InlineData("/home/me/.git/HEAD")]
    [InlineData("/home/me/.git")]
    [InlineData("/.git/HEAD")]
    public void An_agent_folder_inside_a_git_working_tree_selects_nothing(string gitEntry)
    {
        File("/home/me/.claude/projects/p/s1.jsonl", DaysAgo(40));
        File(gitEntry, DaysAgo(40));

        var claude = Claude();

        claude.Due.Should().BeEmpty();
        claude.Skipped.Should().BeEmpty();
        claude.Note.Should().Contain(".git", "the refusal names the repository it found");
    }

    /// <summary>Security review M-1: the home is a link (allowed) into a folder whose parent is a working tree — the spelled path
    /// has no .git above it, the real one has, and the agent selects nothing.</summary>
    [Fact]
    public void An_agent_folder_whose_real_path_lies_in_a_git_working_tree_selects_nothing()
    {
        File("/home/me/.claude/projects/p/s1.jsonl", DaysAgo(40));
        var real = Path.GetFullPath(_sandbox.Paths.DistroPath("/data/me"));
        Directory.CreateDirectory(Path.GetDirectoryName(real)!);
        Directory.Move(Path.GetFullPath(_sandbox.Paths.DistroPath("/home/me")), real);
        File("/data/.git/HEAD", DaysAgo(40));
        Assert.SkipUnless(DirectoryLinks.TryCreate(Path.GetFullPath(_sandbox.Paths.DistroPath("/home/me")), real), "this machine cannot make a directory link");

        var claude = Claude();

        claude.Due.Should().BeEmpty();
        claude.Note.Should().Contain(".git");
    }
}
