using FluentAssertions;

using WslCare.Core.Archive;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Archive;

/// <summary>Plan §15r *E9.S3 own review round* — the restore against the security (S-) and correctness (C-) reviews, each red first.</summary>
public sealed partial class ArchiveProtocolTests
{
    /// <summary>S-B1 (owner rule 2026-10-07): the agent folder was stowed into a dotfiles repository after the archive run (a link on the
    /// way, a <c>.git</c> above its real path) — the restore writes nothing there.</summary>
    [Fact]
    public void A_restore_into_an_agent_folder_stowed_into_a_git_tree_writes_nothing()
    {
        var indexed = Removed("s70");
        var stowed = On("/home/me/dotfiles/claude/.claude");
        Directory.CreateDirectory(Path.GetDirectoryName(stowed)!);
        Directory.Move(On(Layout), stowed);
        Write("/home/me/dotfiles/.git/HEAD", "ref: refs/heads/main");
        Assert.SkipUnless(DirectoryLinks.TryCreate(On(Layout), stowed), "this machine cannot make a directory link");

        var report = Restore(indexed);

        report.Sessions.Single().Outcome.Should().Be(RestoreOutcomes.Refused);
        File.Exists(Path.Combine(stowed, "projects/p/s70.jsonl")).Should().BeFalse("nothing is created inside a working tree");
    }

    /// <summary>S-B1, scenario 2: an unverified entry restored WITH --accept-unverified whose file names a <c>.git</c> segment would
    /// create a repository (a <c>.git/config</c> can run code on the next git command) — refused before any write.</summary>
    [Fact]
    public void An_accepted_unverified_entry_naming_a_git_folder_is_never_restored()
    {
        var indexed = Removed("s71");
        var companion = indexed.Files.Single(f => f.Original != indexed.Key);
        var forged = indexed with { Verified = false, Files = [indexed.Files.Single(f => f.Original == indexed.Key), companion with { Original = "projects/p/s71/.git/config" }] };

        var report = Restore(forged, accept: true);

        report.Sessions.Single().Outcome.Should().Be(RestoreOutcomes.Refused);
        Directory.Exists(Source("projects/p/s71/.git")).Should().BeFalse();
        File.Exists(Source("projects/p/s71.jsonl")).Should().BeFalse("nothing of it is restored");
    }

    /// <summary>S-B2 / C-1: the copy changes between its hash check and the stream (another client of the share appends to it) — the
    /// stream stops at the length the index names, the bytes do not match, and NOTHING lands under the session's name.</summary>
    [Fact]
    public void A_copy_that_changes_after_its_check_never_lands_under_the_sessions_name()
    {
        var indexed = Removed("s72");
        var appended = false;
        var chunks = 0;
        _fault = step =>
        {
            chunks += step == MoveSteps.RestoreChunk ? 1 : 0;
            if (step == MoveSteps.RestoreChunk && !appended)
            {
                appended = true;
                File.AppendAllText(Archived("projects/p/s72.jsonl"), new string('x', 3 * 1024 * 1024));
            }
        };

        var report = Restore(indexed);

        report.Sessions.Single().Outcome.Should().Be(RestoreOutcomes.Refused);
        File.Exists(Source("projects/p/s72.jsonl")).Should().BeFalse("a file that did not match is never left under the real name");
        Directory.EnumerateFiles(On(Layout), "*" + ArchiveNames.RestoreMark + "*", SearchOption.AllDirectories).Should().BeEmpty("the temporary copy is removed");
        chunks.Should().Be(1, "the stream stops as soon as it passes the indexed length — never writes the appended megabytes");
    }

    /// <summary>C-1: the base fails while the SECOND file is written — the first stays restored (it was promoted whole), the second
    /// leaves nothing under its name and no temporary file, so a later restore is not refused for ever.</summary>
    [Fact]
    public void A_failure_mid_restore_leaves_no_partial_file_under_its_name()
    {
        var indexed = Removed("s73");
        var chunks = 0;
        _fault = step =>
        {
            if (step == MoveSteps.RestoreChunk && ++chunks == 2)
            {
                throw new IOException("the share went away");
            }
        };

        var report = Restore(indexed);

        report.Sessions.Single().Outcome.Should().Be(RestoreOutcomes.Refused);
        var restored = new[] { "projects/p/s73.jsonl", "projects/p/s73/subagents/a.jsonl" }.Where(r => File.Exists(Source(r))).ToList();
        restored.Should().ContainSingle("the file written before the failure was promoted whole");
        File.ReadAllText(Source(restored[0])).Should().BeOneOf("the transcript", "a subagent");
        Directory.EnumerateFiles(On(Layout), "*" + ArchiveNames.RestoreMark + "*", SearchOption.AllDirectories).Should().BeEmpty();

        _fault = static _ => { };
        Restore(indexed, runId: "r7").Restored.Should().Be(1, "the next restore finishes it");
    }

    /// <summary>C-4 (the safe default, owner decision pending): a SPLIT entry — a companion the agent changed went back under its name —
    /// restores the files that are MISSING and never touches an existing one; the answer is <c>partial</c> and names what was kept.</summary>
    [Fact]
    public void A_split_entry_restores_only_the_missing_files_and_answers_partial()
    {
        var indexed = Removed("s75") with { Status = ArchiveIndex.Events.Split };
        Write($"{Layout}/projects/p/s75/subagents/a.jsonl", "the companion as the agent changed it");

        var session = Restore(indexed).Sessions.Single();

        session.Outcome.Should().Be(RestoreOutcomes.Partial);
        session.Note.Should().Contain("projects/p/s75/subagents/a.jsonl");
        File.ReadAllText(Source("projects/p/s75.jsonl")).Should().Be("the transcript");
        File.ReadAllText(Source("projects/p/s75/subagents/a.jsonl")).Should().Be("the companion as the agent changed it");
    }

    /// <summary>C-8b: decide-before-write — the conflict on the COMPANION refuses the session, and the transcript (checked first, missing)
    /// was not created either.</summary>
    [Fact]
    public void A_conflict_on_a_companion_refuses_the_session_before_the_transcript_is_written()
    {
        var indexed = Removed("s74");
        Write($"{Layout}/projects/p/s74/subagents/a.jsonl", "the agent's own new subagent");

        Restore(indexed).Sessions.Single().Outcome.Should().Be(RestoreOutcomes.Refused);
        File.Exists(Source("projects/p/s74.jsonl")).Should().BeFalse("nothing is written before every file is decided");
    }
}
