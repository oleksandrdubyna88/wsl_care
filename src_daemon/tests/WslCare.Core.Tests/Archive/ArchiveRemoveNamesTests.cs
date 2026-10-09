using FluentAssertions;

using WslCare.Core.Archive;

namespace WslCare.Core.Tests.Archive;

/// <summary>Plan §15r D2 phase 2, the E9.S5 own review (2): an entry resumed past its commit point has its files under their QUARANTINE
/// names — what an agent holding one through the rename holds — so the liveness check asks those, and the original names too.</summary>
public sealed class ArchiveRemoveNamesTests
{
    [Fact]
    public void A_resumed_entry_is_asked_under_its_quarantine_names_and_its_original_ones()
    {
        var entry = new InflightEntry("0123456789abcdef", "claude-code", "/home/me/.claude", "projects/p/s1.jsonl", "2026/08", InflightStates.Removing, "r1", DateTimeOffset.UnixEpoch, 2, "r7");
        var indexed = IndexEntryOf("projects/p/s1.jsonl", "projects/p/s1/subagents/a.jsonl");

        var names = ArchiveRemove.ResumeNames(entry, indexed);

        names.Should().BeEquivalentTo(
        [
            "projects/p/s1.jsonl",
            "projects/p/s1/subagents/a.jsonl",
            $"projects/p/s1.jsonl{ArchiveNames.QuarantineMark}r7",
            $"projects/p/s1/subagents/a.jsonl{ArchiveNames.QuarantineMark}r7",
        ]);
    }

    private static IndexEntry IndexEntryOf(params string[] originals) =>
        new("0123456789abcdef", "claude-code", originals[0], "2026/08", [.. originals.Select(o => new IndexFile(o, o, 1, new string('0', 64), DateTimeOffset.UnixEpoch))], ArchiveIndex.Events.Archived, true, DateTimeOffset.UnixEpoch);
}
