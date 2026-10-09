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

    /// <summary>Owner rule 2026-10-07, phase 2: a git working tree that APPEARED after the copy — a repository inside the session's
    /// companion folder, or the project folder made a working tree (a worktree's <c>.git</c> file) — keeps the whole unit at the
    /// source; nothing is renamed or removed, and the copies stay a snapshot.</summary>
    [Theory]
    [InlineData("projects/p/s24/subagents/repo/.git/HEAD")]
    [InlineData("projects/p/.git")]
    public void A_git_tree_that_appeared_after_the_copy_keeps_the_whole_session_at_the_source(string gitEntry)
    {
        var entry = Copied(Run("r1"), Session("s24"));
        Write($"{Layout}/{gitEntry}", "gitdir: elsewhere");

        var (later, outcome) = RemoveLater(entry);

        outcome.Should().BeOfType<RemoveOutcome.Superseded>().Which.Why.Should().Contain(".git");
        File.ReadAllText(Source("projects/p/s24.jsonl")).Should().Be("the transcript");
        File.ReadAllText(Source("projects/p/s24/subagents/a.jsonl")).Should().Be("a subagent");
        Directory.EnumerateFiles(On(Layout), "*" + ArchiveNames.QuarantineMark + "*", SearchOption.AllDirectories).Should().BeEmpty();
        Index(later).Single(e => e.EntryId == entry.EntryId).Status.Should().Be(ArchiveIndex.Events.Superseded);
        later.Book.Entries.Should().BeEmpty();
    }

    /// <summary>The same past the commit point: the run stopped at its first removal, a repository appeared in the companion folder,
    /// and the reconcile's resumed removal sends every quarantined file back instead of removing them.</summary>
    [Fact]
    public void A_git_tree_that_appeared_after_the_commit_point_sends_the_whole_session_back()
    {
        var entry = Copied(Run("r1"), Session("s25"));
        var stopped = false;
        _fault = step =>
        {
            if (step == nameof(WslCare.Core.Files.ArchiveFileStep.RemovalOpened) && !stopped)
            {
                stopped = true;
                throw new OperationCanceledException("killed at the first removal");
            }
        };
        var act = () => RemoveLater(entry);
        act.Should().Throw<OperationCanceledException>();
        _fault = static _ => { };
        Write($"{Layout}/projects/p/s25/subagents/repo/.git/HEAD", "ref: refs/heads/main");

        _clock.Advance(TimeSpan.FromHours(25));
        var report = ArchiveReconcile.FromInflight(Run("r3"));

        report.Resumed.Should().Be(0);
        File.ReadAllText(Source("projects/p/s25.jsonl")).Should().Be("the transcript");
        File.ReadAllText(Source("projects/p/s25/subagents/a.jsonl")).Should().Be("a subagent");
        Directory.EnumerateFiles(On(Layout), "*" + ArchiveNames.QuarantineMark + "*", SearchOption.AllDirectories).Should().BeEmpty();
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
