using FluentAssertions;

using WslCare.Core.Agents;
using WslCare.Core.Archive;

namespace WslCare.Core.Tests.Archive;

/// <summary>
/// Plan §15r D6, E9.S3 — the restore over the real disk: an archived and removed session created back under its original names with
/// the archived bytes and the restore time; refused whole when a line is unverified (without <c>--accept-unverified</c>), a copy is
/// damaged, a file of that name holds other bytes, a file lies outside the unit or names what never moves, or the entry is on its
/// way; the copies stay; a restored session archived again is an EVENT only, and the next run removes it against the same copies.
/// </summary>
public sealed partial class ArchiveProtocolTests
{
    private static AgentEntry Claude => AgentCatalogue.Agents.Single(a => a.Id == Agent);

    private ArchiveTarget Target => new(Claude, On(Layout), string.Empty);

    /// <summary>A session copied in one run and removed in the next, as the protocol leaves it.</summary>
    private IndexEntry Removed(string id)
    {
        var entry = Copied(Run("r1"), Session(id));
        var (later, outcome) = RemoveLater(entry);
        outcome.Should().BeOfType<RemoveOutcome.Removed>();
        return Index(later).Single(e => e.EntryId == entry.EntryId);
    }

    private RestoreReport Restore(IndexEntry entry, bool accept = false, string runId = "r5") =>
        ArchiveRestore.Restore(Run(runId), [new ArchiveRestore.Candidate(Target, entry)], accept);

    [Fact]
    public void A_removed_session_is_restored_byte_identical_with_the_restore_time_and_its_copies_stay()
    {
        var indexed = Removed("s60");
        _clock.Advance(TimeSpan.FromDays(3));

        var report = Restore(indexed);

        report.Restored.Should().Be(1, report.Sessions.Single().Note);
        File.ReadAllText(Source("projects/p/s60.jsonl")).Should().Be("the transcript");
        File.ReadAllText(Source("projects/p/s60/subagents/a.jsonl")).Should().Be("a subagent");
        File.GetLastWriteTimeUtc(Source("projects/p/s60.jsonl")).Should().Be(_clock.GetUtcNow().UtcDateTime, "the restore time, so Claude's own sweep does not delete it at once (D6, Q3)");
        File.ReadAllText(Archived("projects/p/s60.jsonl")).Should().Be("the transcript", "the archived copy stays");
        Index(Run("r6")).Single(e => e.EntryId == indexed.EntryId).Status.Should().Be(ArchiveIndex.Events.Restored);
        State.Restored().Should().ContainSingle().Which.EntryId.Should().Be(indexed.EntryId);
    }

    /// <summary>D4/D6: an entry with a line this side did not sign is restored only with <c>--accept-unverified</c>.</summary>
    [Fact]
    public void An_unverified_entry_is_restored_only_when_accepted()
    {
        var indexed = Removed("s61") with { Verified = false };

        Restore(indexed).Sessions.Single().Should().Match<RestoredSession>(s => s.Outcome == RestoreOutcomes.Refused && s.Note.Contains("--accept-unverified"));
        File.Exists(Source("projects/p/s61.jsonl")).Should().BeFalse();

        Restore(indexed, accept: true).Restored.Should().Be(1);
        File.ReadAllText(Source("projects/p/s61.jsonl")).Should().Be("the transcript");
    }

    /// <summary>Never overwrite: a live file of that name with other bytes refuses the WHOLE session — not one file of it is created.</summary>
    [Fact]
    public void A_live_file_of_that_name_with_other_bytes_refuses_the_whole_session()
    {
        var indexed = Removed("s62");
        Write($"{Layout}/projects/p/s62.jsonl", "a new session at that name");

        var report = Restore(indexed);

        report.Sessions.Single().Should().Match<RestoredSession>(s => s.Outcome == RestoreOutcomes.Refused && s.Note.Contains("other bytes"));
        File.ReadAllText(Source("projects/p/s62.jsonl")).Should().Be("a new session at that name");
        File.Exists(Source("projects/p/s62/subagents/a.jsonl")).Should().BeFalse("nothing of the session is restored");
    }

    /// <summary>D6: the archived copy is hashed first — a damaged copy is never restored, and nothing of its session is.</summary>
    [Fact]
    public void A_damaged_copy_is_never_restored_nor_anything_of_its_session()
    {
        var indexed = Removed("s63");
        File.WriteAllText(Archived("projects/p/s63/subagents/a.jsonl"), "rotten");

        Restore(indexed).Sessions.Single().Should().Match<RestoredSession>(s => s.Outcome == RestoreOutcomes.Refused && s.Note.Contains("damaged"));
        File.Exists(Source("projects/p/s63.jsonl")).Should().BeFalse();
    }

    /// <summary>Review M4: an index line naming a file outside the agent's layout — beside the unit, or under what never moves — is
    /// never restored, nor anything of its entry.</summary>
    [Theory]
    [InlineData("settings.json")]
    [InlineData("projects/p/other.jsonl")]
    [InlineData("projects/p/s64/memory/MEMORY.md")]
    public void An_index_line_pointing_outside_the_agents_layout_is_never_restored(string stray)
    {
        var indexed = Removed("s64");
        var forged = indexed with { Files = [.. indexed.Files, indexed.Files[0] with { Original = stray }] };

        Restore(forged).Sessions.Single().Outcome.Should().Be(RestoreOutcomes.Refused);
        File.Exists(Source("projects/p/s64.jsonl")).Should().BeFalse();
        File.Exists(Source(stray)).Should().BeFalse();
    }

    /// <summary>An entry still on its way through a run (in the in-flight file) is not restored under it.</summary>
    [Fact]
    public void An_entry_on_its_way_is_not_restored()
    {
        var entry = Copied(Run("r1"), Session("s65"));
        var indexed = Index(Run("r2")).Single(e => e.EntryId == entry.EntryId);

        Restore(indexed).Sessions.Single().Note.Should().Contain("on its way");
    }

    /// <summary>A session whose files are all there with the archived bytes is "already there": nothing written, no event.</summary>
    [Fact]
    public void A_session_already_there_is_answered_so_and_nothing_is_written()
    {
        var indexed = Removed("s66");
        Restore(indexed).Restored.Should().Be(1);

        var again = Restore(Index(Run("r6")).Single(e => e.EntryId == indexed.EntryId), runId: "r7");

        again.AlreadyThere.Should().Be(1);
        Index(Run("r8")).Single(e => e.EntryId == indexed.EntryId).Status.Should().Be(ArchiveIndex.Events.Restored);
    }

    /// <summary>Coai G5, review M10: the restored session, due again, is archived as an EVENT only — the existing copies named, no bytes
    /// written — it leaves restored.json, and the run after removes it against those same copies.</summary>
    [Fact]
    public void A_restored_session_archived_again_writes_only_an_event_and_is_removed_against_the_same_copies()
    {
        var indexed = Removed("s67");
        Restore(indexed).Restored.Should().Be(1);
        var copies = Directory.EnumerateFiles(On(Base), "*.jsonl", SearchOption.AllDirectories).Count();

        var again = ArchiveCopy.Copy(Run("r9"), Agent, On(Layout), Session("s67"));

        again.Should().BeOfType<CopyOutcome.Archived>().Which.Bytes.Should().Be(0, "no byte is copied again");
        Directory.EnumerateFiles(On(Base), "*.jsonl", SearchOption.AllDirectories).Count().Should().Be(copies, "the existing copies are named, none is added");
        State.Restored().Should().BeEmpty();
        var (later, outcome) = RemoveLater(((CopyOutcome.Archived)again).Entry, "r10");
        outcome.Should().BeOfType<RemoveOutcome.Removed>();
        Index(later).Single(e => e.EntryId == indexed.EntryId).Status.Should().Be(ArchiveIndex.Events.SourceRemoved);
    }

    /// <summary>D6: a restored session that CHANGED before it was due again is copied as any session (new bytes, a new entry).</summary>
    [Fact]
    public void A_restored_session_that_changed_is_copied_again_as_any_session()
    {
        var indexed = Removed("s68");
        Restore(indexed).Restored.Should().Be(1);

        var again = ArchiveCopy.Copy(Run("r9"), Agent, On(Layout), Session("s68", main: "the transcript, resumed"));

        again.Should().BeOfType<CopyOutcome.Archived>().Which.Bytes.Should().BeGreaterThan(0);
        ((CopyOutcome.Archived)again).Entry.EntryId.Should().NotBe(indexed.EntryId);
        State.Restored().Should().BeEmpty();
    }

    [Theory]
    [InlineData("projects/*/*.jsonl", "projects/p/s.jsonl", true)]
    [InlineData("projects/*/*.jsonl", "projects/p/q/s.jsonl", false)]
    [InlineData("projects/*/*.jsonl", "settings.json", false)]
    [InlineData("**/*.log", "a/b/c.log", true)]
    [InlineData("**/*.log", "c.log", true)]
    public void A_layout_glob_matches_one_pattern_per_segment(string glob, string relative, bool matches) =>
        ArchiveRestore.GlobMatches(glob, relative).Should().Be(matches);
}
