using FluentAssertions;

using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;

namespace WslCare.Core.Tests.Files;

/// <summary>
/// The ONE deletion policy (plan §15a C1), as a table over both path families. Paths here are
/// already real; the file-system tests cover how a spelled path becomes one.
/// </summary>
public sealed class DeletionPolicyTests
{
    private static DeletionPolicy LinuxPolicy()
    {
        var paths = new LinuxHostPaths(new LinuxEnvironment("/home/me", "/etc", "/var", "/tmp", "/home/me/.config"));
        return new DeletionPolicy(ProtectedRoots.From(paths, p => p), PathRules.Linux);
    }

    private static DeletionPolicy WindowsPolicy()
    {
        var paths = new WindowsHostPaths(new WindowsEnvironment(
            @"C:\Users\me", @"C:\Users\me\AppData\Roaming", @"C:\Users\me\AppData\Local", @"C:\ProgramData", @"C:\Users\me\AppData\Local\Temp"));
        return new DeletionPolicy(ProtectedRoots.From(paths, p => p), PathRules.Windows);
    }

    private static DeletionRequest Delete(string path, string root, DeletionPermit permit = DeletionPermit.None) =>
        new(FileOperation.Delete, path, string.Empty, root, "test", permit);

    private static DeletionRequest Move(string from, string to, string root, DeletionPermit permit = DeletionPermit.None) =>
        new(FileOperation.Move, from, to, root, "test", permit);

    [Theory]
    [InlineData("/var/log/wsl-care/2026-09-01", "/var/log/wsl-care")]
    [InlineData("/var/lib/docker/volumes/abc/_data", "/var/lib/docker/volumes")]
    [InlineData("/home/me/.npm/_cacache/index-v5", "/home/me/.npm")]
    [InlineData("/home/me/.vscode-server/bin/abc123", "/home/me/.vscode-server")]
    public void Linux_a_path_strictly_inside_the_declared_root_may_be_deleted(string path, string root)
    {
        LinuxPolicy().Decide(Delete(path, root)).IsAllowed.Should().BeTrue();
    }

    [Theory]
    [InlineData("/var/log/wsl-care", "/var/log/wsl-care", DeletionRule.OutsideDeclaredRoot)]
    [InlineData("/var/log/other/x", "/var/log/wsl-care", DeletionRule.OutsideDeclaredRoot)]
    [InlineData("/var/log/wsl-care-old/x", "/var/log/wsl-care", DeletionRule.OutsideDeclaredRoot)]
    [InlineData("/home/me/.claude/projects/p/s.jsonl", "/home/me", DeletionRule.AgentFolder)]
    [InlineData("/home/me/.codex/sessions/2026/x.jsonl", "/home/me/.codex", DeletionRule.AgentFolder)]
    [InlineData("/home/me/.cache/antigravity/x", "/home/me/.cache", DeletionRule.AgentFolder)]
    [InlineData("/home/me/git/repo/bin", "/home/me/git/repo", DeletionRule.GitFolder)]
    [InlineData("/home/me/git/_wt/x", "/home/me", DeletionRule.GitFolder)]
    [InlineData("/tmp/claude/shell-snapshots/x", "/tmp", DeletionRule.ClaudeTemp)]
    [InlineData("/etc/x", "/", DeletionRule.RootTooBroad)]
    [InlineData("/home/me/anything", "/home/me", DeletionRule.RootTooBroad)]
    public void Linux_the_never_list_refuses_by_the_named_rule(string path, string root, DeletionRule expected)
    {
        var verdict = LinuxPolicy().Decide(Delete(path, root));

        verdict.Should().BeOfType<DeletionVerdict.Refused>().Which.Rule.Should().Be(expected);
    }

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\wsl-care\logs\2026-09-01", @"C:\Users\me\AppData\Local\wsl-care\logs")]
    [InlineData(@"C:\Users\me\AppData\Local\Temp\nuget\x", @"C:\Users\me\AppData\Local\Temp")]
    public void Windows_a_path_strictly_inside_the_declared_root_may_be_deleted(string path, string root)
    {
        WindowsPolicy().Decide(Delete(path, root)).IsAllowed.Should().BeTrue();
    }

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\Temp\claude\x", @"C:\Users\me\AppData\Local\Temp", DeletionRule.ClaudeTemp)]
    [InlineData(@"C:\Users\me\AppData\Local\Temp\CLAUDE\x", @"C:\Users\me\AppData\Local\Temp", DeletionRule.ClaudeTemp)]
    [InlineData(@"C:\Users\me\.claude\projects\p\s.jsonl", @"C:\Users\me", DeletionRule.AgentFolder)]
    [InlineData(@"C:\Users\me\AppData\Local\AnthropicClaude\x", @"C:\Users\me\AppData\Local", DeletionRule.AgentFolder)]
    [InlineData(@"C:\Users\me\AppData\Roaming\Claude\x", @"C:\Users\me\AppData\Roaming", DeletionRule.AgentFolder)]
    [InlineData(@"C:\Users\me\git\repo\bin", @"C:\Users\me\git", DeletionRule.GitFolder)]
    [InlineData(@"D:\other\x", @"C:\Users\me\AppData\Local\Temp", DeletionRule.OutsideDeclaredRoot)]
    [InlineData(@"C:\Windows\Temp\x", @"C:\", DeletionRule.RootTooBroad)]
    public void Windows_the_never_list_refuses_by_the_named_rule_case_insensitively(string path, string root, DeletionRule expected)
    {
        var verdict = WindowsPolicy().Decide(Delete(path, root));

        verdict.Should().BeOfType<DeletionVerdict.Refused>().Which.Rule.Should().Be(expected);
    }

    [Fact]
    public void The_archive_permit_allows_a_move_out_of_an_agent_folder_into_the_archive_root()
    {
        var request = Move("/home/me/.claude/projects/p/s.jsonl", "/mnt/archive/claude/2026/09/s.jsonl", "/mnt/archive", DeletionPermit.MoveOutOfAgentFolder);

        LinuxPolicy().Decide(request).IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void The_archive_permit_never_allows_a_delete_inside_an_agent_folder()
    {
        var verdict = LinuxPolicy().Decide(Delete("/home/me/.claude/projects/p/s.jsonl", "/mnt/archive", DeletionPermit.MoveOutOfAgentFolder));

        verdict.Should().BeOfType<DeletionVerdict.Refused>().Which.Rule.Should().Be(DeletionRule.AgentFolder);
    }

    [Theory]
    [InlineData("/home/me/.claude/projects/p1/memory/MEMORY.md")]
    [InlineData("/home/me/.claude/projects/p1/memory")]
    [InlineData("/home/me/.claude/projects/p1/memory/notes/x.md")]
    public void Agent_memory_is_never_moved_even_with_the_archive_permit(string path)
    {
        var verdict = LinuxPolicy().Decide(Move(path, "/mnt/archive/claude/x", "/mnt/archive", DeletionPermit.MoveOutOfAgentFolder));

        verdict.Should().BeOfType<DeletionVerdict.Refused>().Which.Rule.Should().Be(DeletionRule.AgentMemory);
    }

    [Fact]
    public void A_session_folder_beside_memory_is_not_memory()
    {
        var verdict = LinuxPolicy().Decide(Move("/home/me/.claude/projects/p1/abc.jsonl", "/mnt/archive/claude/abc.jsonl", "/mnt/archive", DeletionPermit.MoveOutOfAgentFolder));

        verdict.IsAllowed.Should().BeTrue();
    }

    [Theory]
    [InlineData("/home/me/.codex/x", DeletionRule.ProtectedDestination)]
    [InlineData("/home/me/git/x", DeletionRule.ProtectedDestination)]
    [InlineData("/tmp/claude/x", DeletionRule.ProtectedDestination)]
    [InlineData("/mnt/elsewhere/x", DeletionRule.OutsideDeclaredRoot)]
    public void A_move_destination_is_judged_too(string destination, DeletionRule expected)
    {
        var verdict = LinuxPolicy().Decide(Move("/mnt/archive/staging/x", destination, "/mnt/archive"));

        verdict.Should().BeOfType<DeletionVerdict.Refused>().Which.Rule.Should().Be(expected);
    }

    [Fact]
    public void A_refusal_names_the_action_and_the_path_so_the_log_line_is_readable()
    {
        var verdict = LinuxPolicy().Decide(new DeletionRequest(FileOperation.Delete, "/home/me/git/repo", string.Empty, "/home/me/git", "A14", DeletionPermit.None));

        var refused = verdict.Should().BeOfType<DeletionVerdict.Refused>().Subject;
        refused.Reason.Should().Contain("A14").And.Contain("/home/me/git/repo");
    }
}
