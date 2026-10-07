using FluentAssertions;

using WslCare.Core.Archive;

namespace WslCare.Core.Tests.Archive;

/// <summary>
/// Plan §15r E9.S2b acceptance — sessions of every shape go through both phases whole: an empty transcript (0 bytes) and a session
/// with many small companion files.
/// </summary>
public sealed partial class ArchiveProtocolTests
{
    [Fact]
    public void An_empty_transcript_is_copied_indexed_and_removed_like_any_other()
    {
        var entry = Copied(Run("r1"), Session("s20", main: string.Empty, companion: string.Empty));

        File.Exists(Archived("projects/p/s20.jsonl")).Should().BeTrue();
        new FileInfo(Archived("projects/p/s20.jsonl")).Length.Should().Be(0);

        var (_, outcome) = RemoveLater(entry);

        outcome.Should().BeOfType<RemoveOutcome.Removed>();
        File.Exists(Source("projects/p/s20.jsonl")).Should().BeFalse();
    }

    /// <summary>D3: an entry whose latest index status is <c>restored</c> is never removed — the restored session is live again.</summary>
    [Fact]
    public void A_restored_session_is_never_removed_by_a_later_run()
    {
        var entry = Copied(Run("r1"), Session("s22"));
        var c = Run("r1");
        var restored = new IndexLine(ArchiveIndex.SchemaVersion, ArchiveIndex.Events.Restored, entry.EntryId, Agent, Side, entry.Key, Month, DateTimeOffset.UnixEpoch, "UTC", "r1", [], string.Empty);
        ArchiveCopy.AppendLine(c, Agent, Month, restored).Should().BeEmpty();

        var (later, outcome) = RemoveLater(entry);

        outcome.Should().BeOfType<RemoveOutcome.Dropped>();
        File.ReadAllText(Source("projects/p/s22.jsonl")).Should().Be("the transcript");
        later.Book.Entries.Should().BeEmpty();
    }

    /// <summary>Review M5: a removal never acts on an index line this side did not sign (an edited line, a planted one).</summary>
    [Fact]
    public void A_removal_never_acts_on_an_unverified_index_line()
    {
        var entry = Copied(Run("r1"), Session("s23"));
        var index = Archived(ArchiveIndex.FileName);
        File.WriteAllText(index, File.ReadAllText(index).Replace("\"r1\"", "\"rX\"", StringComparison.Ordinal));

        var (_, outcome) = RemoveLater(entry);

        outcome.Should().BeOfType<RemoveOutcome.Kept>().Which.Why.Should().Contain("MAC");
        File.ReadAllText(Source("projects/p/s23.jsonl")).Should().Be("the transcript");
    }

    [Fact]
    public void A_session_with_many_small_files_moves_whole()
    {
        var files = new List<UnitFile> { File1("projects/p/s21.jsonl", "the transcript") };
        files.AddRange(Enumerable.Range(0, 120).Select(i => File1($"projects/p/s21/subagents/agent-{i:000}.jsonl", $"subagent {i}")));
        var unit = new UnitFound("session", "projects/p/s21.jsonl", files, files.Max(f => f.LastWriteUtc), Month, string.Empty, string.Empty);

        var entry = Copied(Run("r1"), unit);
        var (later, outcome) = RemoveLater(entry);

        outcome.Should().BeOfType<RemoveOutcome.Removed>().Which.Files.Should().Be(121);
        Directory.Exists(Source("projects/p/s21")).Should().BeFalse("every companion went, and the folder they left with them");
        Index(later).Single(e => e.EntryId == entry.EntryId).Files.Should().HaveCount(121);
        Enumerable.Range(0, 120).Should().OnlyContain(i => File.ReadAllText(Archived($"projects/p/s21/subagents/agent-{i:000}.jsonl")) == $"subagent {i}");
    }
}
