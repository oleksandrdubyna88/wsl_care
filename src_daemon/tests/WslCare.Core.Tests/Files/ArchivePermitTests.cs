using FluentAssertions;

using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;

namespace WslCare.Core.Tests.Files;

/// <summary>
/// Plan §15r E9.S2a, H1 — the deletion policy's archive permits as a table: under an AI agent's folder the product only ever
/// renames a session file aside to its quarantine name in its own folder and back, removes a QUARANTINED file whose archived copy
/// is named outside every protected place (the seam then removes it only when its bytes hash equal), removes an EMPTY folder strictly
/// inside the agent's folder, and creates a restored file — never a plain delete, never the agent's folder itself, never memory.
/// </summary>
public sealed class ArchivePermitTests
{
    private const string Layout = "/home/me/.claude";
    private const string Session = "/home/me/.claude/projects/p/s1.jsonl";
    private const string Quarantined = "/home/me/.claude/projects/p/s1.jsonl.wsl-care-q-r1";
    private const string Copy = "/mnt/v/ai-archive/claude-code/2026/09/wsl-host-ubuntu/projects/p/s1.jsonl";

    private static DeletionPolicy Policy()
    {
        var paths = new LinuxHostPaths(new LinuxEnvironment("/home/me", "/etc", "/var", "/tmp", "/home/me/.config"));
        return new DeletionPolicy(ProtectedRoots.From(paths, p => p), PathRules.Linux);
    }

    private static DeletionVerdict Decide(FileOperation operation, string path, string destination, DeletionPermit permit, string archivedCopy = "", bool folder = false, string root = Layout) =>
        Policy().Decide(new DeletionRequest(operation, path, destination, root, "A13", permit) { ArchivedCopy = archivedCopy, IsFolder = folder });

    private static DeletionRule RuleOf(DeletionVerdict verdict) => verdict.Should().BeOfType<DeletionVerdict.Refused>().Subject.Rule;

    [Fact]
    public void A_session_file_may_be_renamed_to_its_quarantine_name_in_its_own_folder_and_back()
    {
        Decide(FileOperation.Move, Session, Quarantined, DeletionPermit.ArchiveQuarantine).IsAllowed.Should().BeTrue();
        Decide(FileOperation.Move, Quarantined, Session, DeletionPermit.ArchiveQuarantine).IsAllowed.Should().BeTrue();
    }

    [Theory]
    [InlineData(Session, "/home/me/.claude/projects/p/other.jsonl")]
    [InlineData(Session, "/home/me/.claude/projects/q/s1.jsonl.wsl-care-q-r1")]
    [InlineData(Session, "/mnt/v/ai-archive/s1.jsonl.wsl-care-q-r1")]
    [InlineData(Quarantined, "/home/me/.claude/projects/p/s2.jsonl")]
    [InlineData(Session, "/home/me/.claude/projects/p/s1.jsonl.wsl-care-q-")]
    public void A_quarantine_rename_to_any_other_name_or_folder_is_refused(string from, string to)
    {
        RuleOf(Decide(FileOperation.Move, from, to, DeletionPermit.ArchiveQuarantine)).Should().Be(DeletionRule.ArchiveShape);
    }

    [Fact]
    public void A_quarantined_file_with_its_archived_copy_named_may_be_removed()
    {
        Decide(FileOperation.Delete, Quarantined, string.Empty, DeletionPermit.ArchiveRemoval, Copy).IsAllowed.Should().BeTrue();
    }

    [Theory]
    [InlineData(Session, Copy, "a file not renamed aside to its quarantine name")]
    [InlineData(Quarantined, "", "no archived copy")]
    [InlineData(Quarantined, "/home/me/.codex/x", "a copy inside an agent's folder")]
    [InlineData(Quarantined, "/home/me/git/x", "a copy inside the repositories")]
    [InlineData(Quarantined, "relative/copy", "a copy that is no absolute path")]
    public void A_removal_under_an_agent_folder_without_its_quarantine_name_and_an_outside_copy_is_refused(string path, string copy, string why)
    {
        RuleOf(Decide(FileOperation.Delete, path, string.Empty, DeletionPermit.ArchiveRemoval, copy)).Should().Be(DeletionRule.ArchiveShape, why);
    }

    [Fact]
    public void An_empty_folder_strictly_inside_an_agent_folder_may_be_removed_but_never_the_agent_folder_itself()
    {
        Decide(FileOperation.Delete, "/home/me/.claude/projects/p/s1", string.Empty, DeletionPermit.ArchiveRemoval, folder: true).IsAllowed.Should().BeTrue();
        RuleOf(Decide(FileOperation.Delete, "/home/me/.claude", string.Empty, DeletionPermit.ArchiveRemoval, folder: true, root: "/home/me")).Should().Be(DeletionRule.ArchiveShape);
        RuleOf(Decide(FileOperation.Delete, Layout, string.Empty, DeletionPermit.ArchiveRemoval, folder: true)).Should().Be(DeletionRule.ArchiveShape);
    }

    [Fact]
    public void A_plain_delete_under_an_agent_folder_stays_refused_by_every_archive_permit()
    {
        foreach (var permit in new[] { DeletionPermit.ArchiveQuarantine, DeletionPermit.RestoreIntoAgentFolder, DeletionPermit.MoveOutOfAgentFolder })
        {
            RuleOf(Decide(FileOperation.Delete, Quarantined, string.Empty, permit, Copy)).Should().Be(DeletionRule.AgentFolder, permit.ToString());
        }
    }

    [Fact]
    public void A_restore_may_create_under_an_agent_folder_and_nothing_else_may()
    {
        Decide(FileOperation.Create, Session, string.Empty, DeletionPermit.RestoreIntoAgentFolder).IsAllowed.Should().BeTrue();
        RuleOf(Decide(FileOperation.Create, Session, string.Empty, DeletionPermit.None)).Should().Be(DeletionRule.AgentFolder);
        RuleOf(Decide(FileOperation.Create, Session, string.Empty, DeletionPermit.ArchiveRemoval)).Should().Be(DeletionRule.AgentFolder);
    }

    [Theory]
    [InlineData(FileOperation.Move, "/home/me/.claude/projects/p/memory/a.md", "/home/me/.claude/projects/p/memory/a.md.wsl-care-q-r1", DeletionPermit.ArchiveQuarantine)]
    [InlineData(FileOperation.Delete, "/home/me/.claude/projects/p/memory/a.md.wsl-care-q-r1", "", DeletionPermit.ArchiveRemoval)]
    [InlineData(FileOperation.Delete, "/home/me/.claude/projects/p/memory", "", DeletionPermit.ArchiveRemoval)]
    [InlineData(FileOperation.Create, "/home/me/.claude/projects/p/memory/a.md", "", DeletionPermit.RestoreIntoAgentFolder)]
    public void Memory_is_never_touched_by_any_archive_permit(FileOperation operation, string path, string destination, DeletionPermit permit)
    {
        RuleOf(Decide(operation, path, destination, permit, Copy, folder: path.EndsWith("memory", StringComparison.Ordinal))).Should().Be(DeletionRule.AgentMemory);
    }

    [Fact]
    public void A_create_in_the_archive_base_is_judged_inside_its_root_and_never_in_a_protected_place()
    {
        Decide(FileOperation.Create, Copy, string.Empty, DeletionPermit.None, root: "/mnt/v/ai-archive").IsAllowed.Should().BeTrue();
        RuleOf(Decide(FileOperation.Create, "/mnt/other/x", string.Empty, DeletionPermit.None, root: "/mnt/v/ai-archive")).Should().Be(DeletionRule.OutsideDeclaredRoot);
        RuleOf(Decide(FileOperation.Create, "/home/me/git/x", string.Empty, DeletionPermit.None, root: "/home/me/git")).Should().Be(DeletionRule.GitFolder);
    }
}
