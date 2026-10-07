using FluentAssertions;

using WslCare.Core.Archive;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Archive;

/// <summary>
/// Plan §15r *E9.S2b own review round* — the correctness review of 34891b5, each finding red first.
/// </summary>
public sealed partial class ArchiveProtocolTests
{
    /// <summary>M3: a run killed mid-append leaves the month index ending in a torn line; the next append starts on a line of its own,
    /// so the fact it records (here the close line) is never glued onto the torn bytes and lost.</summary>
    [Fact]
    public void The_append_after_a_torn_index_line_starts_on_a_line_of_its_own()
    {
        var entry = Copied(Run("r1"), Session("s40"));
        File.AppendAllText(Archived(ArchiveIndex.FileName), "{\"v\":1,\"event\":\"arch");

        var (later, outcome) = RemoveLater(entry);

        outcome.Should().BeOfType<RemoveOutcome.Removed>();
        Index(later).Single(e => e.EntryId == entry.EntryId).Status.Should().Be(ArchiveIndex.Events.SourceRemoved, "the close line stands on its own line");
        File.ReadAllText(Archived(ArchiveIndex.FileName)).Should().EndWith("\n");
    }

    /// <summary>Security M-1 (owner rule 2026-10-07): after the copy the user stows the agent folder into a dotfiles repository —
    /// <c>~/.claude</c> becomes a link to <c>~/dotfiles/claude/.claude</c> and <c>~/dotfiles/.git</c> exists. The spelled path has
    /// no <c>.git</c> above it, the real one has: phase 2 never acts, and nothing of the session is renamed or removed.</summary>
    [Fact]
    public void A_session_whose_agent_folder_was_stowed_into_a_git_tree_is_never_removed()
    {
        var entry = Copied(Run("r1"), Session("s43"));
        var stowed = On("/home/me/dotfiles/claude/.claude");
        Directory.CreateDirectory(Path.GetDirectoryName(stowed)!);
        Directory.Move(On(Layout), stowed);
        Write("/home/me/dotfiles/.git/HEAD", "ref: refs/heads/main");
        Assert.SkipUnless(DirectoryLinks.TryCreate(On(Layout), stowed), "this machine cannot make a directory link");

        var (_, outcome) = RemoveLater(entry);

        outcome.Should().NotBeOfType<RemoveOutcome.Removed>();
        File.ReadAllText(Path.Combine(stowed, "projects/p/s43.jsonl")).Should().Be("the transcript");
        File.ReadAllText(Path.Combine(stowed, "projects/p/s43/subagents/a.jsonl")).Should().Be("a subagent");
        Directory.EnumerateFiles(stowed, "*" + ArchiveNames.QuarantineMark + "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    /// <summary>Security M-1, the place itself: the agent folder became a link (to a folder in no repository) — phase 2 never acts
    /// where the selection would refuse to walk: it does not even start the quarantine.</summary>
    [Fact]
    public void A_session_whose_agent_folder_became_a_link_is_never_touched()
    {
        var entry = Copied(Run("r1"), Session("s53"));
        var moved = On("/home/me/elsewhere/.claude");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        Directory.Move(On(Layout), moved);
        Assert.SkipUnless(DirectoryLinks.TryCreate(On(Layout), moved), "this machine cannot make a directory link");
        _steps.Clear();

        var (_, outcome) = RemoveLater(entry);

        outcome.Should().BeOfType<RemoveOutcome.Kept>().Which.Why.Should().Contain("selection may walk");
        _steps.Should().NotContain(MoveSteps.QuarantineStart, "nothing is touched behind a link");
        File.ReadAllText(Path.Combine(moved, "projects/p/s53.jsonl")).Should().Be("the transcript");
    }

    /// <summary>Security M-1, the git check on the REAL path: the home itself is a link (allowed — the selection judges below the home's
    /// real path) into a folder whose parent is a working tree. The spelled path has no <c>.git</c> above it; the real one has.</summary>
    [Fact]
    public void A_session_whose_home_really_lies_in_a_git_tree_is_never_removed()
    {
        var entry = Copied(Run("r1"), Session("s44"));
        var real = On("/data/me");
        Directory.CreateDirectory(Path.GetDirectoryName(real)!);
        Directory.Move(On("/home/me"), real);
        Write("/data/.git/HEAD", "ref: refs/heads/main");
        Assert.SkipUnless(DirectoryLinks.TryCreate(On("/home/me"), real), "this machine cannot make a directory link");

        var (_, outcome) = RemoveLater(entry);

        outcome.Should().BeOfType<RemoveOutcome.Superseded>().Which.Why.Should().Contain(".git");
        File.ReadAllText(Path.Combine(real, ".claude/projects/p/s44.jsonl")).Should().Be("the transcript");
    }

    /// <summary>Correctness review M1 (consult row 3, "repair into a new destination"): a damaged copy is found by phase 2, the next run
    /// copies the session again — under a new name, with the same entry id — and the run after removes the source against the REPAIRED
    /// copy; every byte survives both restarts.</summary>
    [Fact]
    public void A_damaged_copy_is_repaired_into_a_new_name_and_the_next_run_removes_against_it()
    {
        var entry = Copied(Run("r1"), Session("s45"));
        File.WriteAllText(Archived("projects/p/s45.jsonl"), "rotten bytes");
        RemoveLater(entry).Outcome.Should().BeOfType<RemoveOutcome.Damaged>();

        var again = Copied(Run("r3"), Session("s45"));
        again.EntryId.Should().Be(entry.EntryId, "the id names the session and its source hashes");
        var (later, outcome) = RemoveLater(again, "r4");

        outcome.Should().BeOfType<RemoveOutcome.Removed>();
        File.Exists(Source("projects/p/s45.jsonl")).Should().BeFalse();
        var merged = Index(later).Single(e => e.EntryId == entry.EntryId);
        merged.Status.Should().Be(ArchiveIndex.Events.SourceRemoved);
        merged.Files.Select(f => File.ReadAllText(Archived(f.Archived))).Should().BeEquivalentTo(["the transcript", "a subagent"], "the index names the repaired copies");
    }

    /// <summary>Security m-2: the append that CREATES a month index flushes the side folder too (the new entry is durable); an append
    /// to an index that exists does not need to.</summary>
    [Fact]
    public void An_index_the_append_created_is_flushed_into_its_folder()
    {
        _ = Copied(Run("r1"), Session("s41"));
        var first = _steps.IndexOf(nameof(WslCare.Core.Files.ArchiveFileStep.Appended));
        _steps[first + 1].Should().Be(nameof(WslCare.Core.Files.ArchiveFileStep.FolderFlushed), "the new index's entry is flushed into its folder");

        _steps.Clear();
        _ = Copied(Run("r1"), Session("s42"));
        var second = _steps.IndexOf(nameof(WslCare.Core.Files.ArchiveFileStep.Appended));
        _steps.ElementAtOrDefault(second + 1).Should().NotBe(nameof(WslCare.Core.Files.ArchiveFileStep.FolderFlushed));
    }

    /// <summary>Security review M-2: phase 2 asks the open-file scan again — Claude Code working in the session's project keeps the
    /// whole unit untouched (Claude keeps no transcript open, so only its working directory says it is there).</summary>
    [Fact]
    public void Phase_2_never_touches_a_session_whose_project_claude_code_works_in()
    {
        var entry = Copied(Run("r1"), Session("s46"));
        _inUse = InUseView.Complete(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "p" });

        var (later, outcome) = RemoveLater(entry);

        outcome.Should().BeOfType<RemoveOutcome.Kept>().Which.Why.Should().Contain("working in the project p");
        File.ReadAllText(Source("projects/p/s46.jsonl")).Should().Be("the transcript");
        Directory.EnumerateFiles(On(Layout), "*" + ArchiveNames.QuarantineMark + "*", SearchOption.AllDirectories).Should().BeEmpty();
        later.Book.Entries.Should().ContainSingle().Which.State.Should().Be(InflightStates.Archived);
    }

    /// <summary>Security review M-2: the agent resumed the session by its path while it was aside — a new file at the original name.
    /// The quarantined history is removed (it is archived), the new file stays, and the entry closes <c>split</c>, never
    /// <c>sourceRemoved</c>.</summary>
    [Fact]
    public void A_file_written_at_the_original_name_while_aside_closes_the_entry_split()
    {
        var entry = Copied(Run("r1"), Session("s47"));
        var written = false;
        _fault = step =>
        {
            if (step == nameof(WslCare.Core.Files.ArchiveFileStep.ArchivedCopyHashed) && !written)
            {
                written = true;
                File.WriteAllText(Source("projects/p/s47.jsonl"), "a new start");
            }
        };

        var (later, outcome) = RemoveLater(entry);

        outcome.Should().BeOfType<RemoveOutcome.Removed>().Which.Event.Should().Be(ArchiveIndex.Events.Split);
        File.ReadAllText(Source("projects/p/s47.jsonl")).Should().Be("a new start");
        Index(later).Single(e => e.EntryId == entry.EntryId).Status.Should().Be(ArchiveIndex.Events.Split);
    }

    /// <summary>Correctness review M8: the base fails while a copy is written (an I/O error from the target) — an answer, not an
    /// exception: the run stops with <c>base-failed</c>, our partial copy is removed, the source is untouched.</summary>
    [Fact]
    public void A_base_that_fails_mid_copy_stops_the_run_and_leaves_no_partial_copy()
    {
        var failed = false;
        _fault = step =>
        {
            if (step == MoveSteps.CopyChunk && !failed)
            {
                failed = true;
                throw new IOException("the share went away");
            }
        };

        var outcome = ArchiveCopy.Copy(Run("r1"), Agent, On(Layout), Session("s50"));

        outcome.Should().BeOfType<CopyOutcome.Stop>().Which.Kind.Should().Be(StopKinds.BaseFailed);
        File.Exists(Archived("projects/p/s50.jsonl")).Should().BeFalse("our partial copy was removed");
        File.ReadAllText(Source("projects/p/s50.jsonl")).Should().Be("the transcript");
        Run("r2").Book.Entries.Should().BeEmpty();
    }

    /// <summary>Plan §15r *Test plan*, review m6: after a removal every file that left the source is in the archive — an archived copy
    /// the index recorded holds exactly the bytes the source held, hashed again from the disk.</summary>
    [Fact]
    public void Every_removed_file_equals_an_archived_copy_the_index_recorded()
    {
        var unit = Session("s51");
        var before = unit.Files.ToDictionary(f => f.Relative, f => Sha(File.ReadAllText(Source(f.Relative))), StringComparer.Ordinal);
        var entry = Copied(Run("r1"), unit);

        var (later, outcome) = RemoveLater(entry);

        outcome.Should().BeOfType<RemoveOutcome.Removed>();
        var recorded = Index(later).Single(e => e.EntryId == entry.EntryId).Files;
        foreach (var (relative, sha) in before)
        {
            File.Exists(Source(relative)).Should().BeFalse();
            recorded.Should().Contain(f => f.Original == relative && f.Sha256 == sha && Sha(File.ReadAllText(Archived(f.Archived))) == sha, $"{relative} is in the archive, byte for byte");
        }
    }

    /// <summary>Correctness review M6: an index that exists and cannot be read is not an empty one — the scan re-indexes nothing of
    /// that month (it would add a recovered line for every file the index already names) and says why.</summary>
    [Fact]
    public void The_scan_skips_a_month_whose_index_cannot_be_read()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && Environment.UserName != "root", "a mode bit makes a file unreadable to a normal user on Linux");
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        _ = Copied(Run("r1"), Session("s52"));
        File.SetUnixFileMode(Archived(ArchiveIndex.FileName), UnixFileMode.UserWrite);

        var report = ArchiveScan.Scan(Run("r2"), Files, [Agent], TestContext.Current.CancellationToken);

        report.Recovered.Should().Be(0);
        report.Notes.Should().Contain(n => n.Contains("could not be read", StringComparison.Ordinal));
    }

    /// <summary>Correctness review M5: an entry phase 2 keeps waiting (here: an index line edited, so unverified) is let go once it has
    /// waited <c>archive.keptEntryDays</c> since it was archived — dropped, its source where it is.</summary>
    [Fact]
    public void An_entry_kept_past_its_days_is_let_go_and_its_source_stays()
    {
        var entry = Copied(Run("r1"), Session("s48"));
        var index = Archived(ArchiveIndex.FileName);
        File.WriteAllText(index, File.ReadAllText(index).Replace("\"r1\"", "\"rX\"", StringComparison.Ordinal));
        var (soon, kept) = RemoveLater(entry);
        ArchiveRemove.LetGo(soon, soon.Book.Entries.Single(), kept).Should().BeOfType<RemoveOutcome.Kept>("one day is not fourteen");

        _clock.Advance(TimeSpan.FromDays(14));
        var (later, again) = RemoveLater(entry);
        var let = ArchiveRemove.LetGo(later, later.Book.Entries.Single(), again);

        let.Should().BeOfType<RemoveOutcome.Dropped>().Which.Why.Should().Contain("keptEntryDays");
        later.Book.Entries.Should().BeEmpty();
        File.ReadAllText(Source("projects/p/s48.jsonl")).Should().Be("the transcript");
    }

    /// <summary>Correctness review M5, the worst case: a <c>removing</c> entry (past its commit point) whose month index became
    /// unreadable is let go after <c>archive.keptEntryDays</c> — and every file still under ITS quarantine name goes back, so the agent
    /// sees its session again.</summary>
    [Fact]
    public void A_removing_entry_whose_index_became_unreadable_returns_its_files_when_let_go()
    {
        var entry = Copied(Run("r1"), Session("s49"));
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
        File.WriteAllText(Archived(ArchiveIndex.FileName), "not an index\n");

        _clock.Advance(TimeSpan.FromDays(15));
        var report = ArchiveReconcile.FromInflight(Run("r3"));

        report.Dropped.Should().Be(1);
        File.ReadAllText(Source("projects/p/s49/subagents/a.jsonl")).Should().Be("a subagent");
        Directory.EnumerateFiles(On(Layout), "*" + ArchiveNames.QuarantineMark + "*", SearchOption.AllDirectories).Should().BeEmpty("every file went back");
        File.Exists(Source("projects/p/s49.jsonl")).Should().BeTrue("the transcript went back too — or was never removed");
    }
}
