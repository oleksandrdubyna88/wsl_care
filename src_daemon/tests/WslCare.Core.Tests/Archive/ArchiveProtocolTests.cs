using System.Security.Cryptography;
using System.Text;

using FluentAssertions;

using WslCare.Core.Archive;
using WslCare.Core.Files;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Archive;

/// <summary>
/// Plan §15r E9.S2b — the move protocol over the real disk (the distro's layout in a sandbox, on the OS that runs the test): phase 1
/// copies, verifies and indexes touching nothing at the source; phase 2, a later run, re-hashes, quarantines, commits and removes;
/// the reconcile finishes what a crash left. Every protocol and seam step can be stopped by the fault seam.
/// </summary>
public sealed partial class ArchiveProtocolTests : IDisposable
{
    private const string Layout = "/home/me/.claude";
    private const string Base = "/mnt/v/ai-archive";
    private const string Agent = "claude-code";
    private const string Side = "wsl-host-distro";
    private const string Month = "2026/09";

    private readonly LinuxSandbox _sandbox = new("archive-protocol");
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly List<string> _steps = [];
    private Action<string> _fault = static _ => { };

    public ArchiveProtocolTests()
    {
        Files = new PhysicalFileSystem(_sandbox.Paths, PhysicalFileSystem.ReadLinkTarget, static (_, _) => { }, (step, path) => Hit(step.ToString(), path));
        Directory.CreateDirectory(On(Base));
        State = new ArchiveState(_sandbox.Paths, Files);
        State.EnsureFolder();
        Key = State.IndexKey();
    }

    private PhysicalFileSystem Files { get; }

    private ArchiveState State { get; }

    private byte[] Key { get; }

    public void Dispose() => _sandbox.Dispose();

    private void Hit(string step, string path)
    {
        _steps.Add(step);
        _fault(step);
    }

    private string On(string distro) => Path.GetFullPath(_sandbox.Paths.DistroPath(distro));

    private string Write(string distro, string content) => Path.GetFullPath(_sandbox.Write(distro, content));

    private static string Sha(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>A run's context: its own run id, a fresh in-flight book read from the state (as a new process would).</summary>
    private MoveContext Run(string runId) => new(Files, On(Base), Side, runId, Key, _clock, TimeZoneInfo.Utc, new InflightBook(State), step => Hit(step, string.Empty), static (_, _) => { }) { Stats = Files };

    /// <summary>A Claude session <c>projects/p/&lt;id&gt;.jsonl</c> with a subagent companion, written, as the selection would find it.</summary>
    private UnitFound Session(string id, string main = "the transcript", string companion = "a subagent")
    {
        var files = new List<UnitFile>
        {
            File1($"projects/p/{id}.jsonl", main),
            File1($"projects/p/{id}/subagents/a.jsonl", companion),
        };
        return new UnitFound("session", $"projects/p/{id}.jsonl", files, files.Max(f => f.LastWriteUtc), Month, string.Empty, string.Empty);
    }

    private UnitFile File1(string relative, string content)
    {
        var path = Write($"{Layout}/{relative}", content);
        var written = new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(path, written.UtcDateTime);
        return new UnitFile(relative, path, Encoding.UTF8.GetByteCount(content), written);
    }

    private string Source(string relative) => On($"{Layout}/{relative}");

    private string Archived(string relative) => On($"{Base}/{Agent}/{Month}/{Side}/{relative}");

    private IReadOnlyList<IndexEntry> Index(MoveContext c) => MonthIndex.Entries(c, Agent, Month);

    private InflightEntry Copied(MoveContext c, UnitFound unit) =>
        ArchiveCopy.Copy(c, Agent, On(Layout), unit).Should().BeOfType<CopyOutcome.Archived>().Subject.Entry;

    /// <summary>The later run: the clock past <c>archive.removeAfterHours</c>, a new run id, the entry looked up in the index.</summary>
    private (MoveContext Context, RemoveOutcome Outcome) RemoveLater(InflightEntry entry, string runId = "r2")
    {
        _clock.Advance(TimeSpan.FromHours(25));
        var later = Run(runId);
        var indexed = Index(later).Single(e => e.EntryId == entry.EntryId);
        return (later, ArchiveRemove.Remove(later, later.Book.Entries.Single(e => e.EntryId == entry.EntryId), indexed));
    }

    [Fact]
    public void The_source_is_removed_only_by_a_later_run_after_its_copy_is_re_hashed()
    {
        var unit = Session("s1");
        var first = Run("r1");

        var entry = Copied(first, unit);

        File.Exists(Source("projects/p/s1.jsonl")).Should().BeTrue("phase 1 touches nothing at the source");
        File.ReadAllText(Archived("projects/p/s1.jsonl")).Should().Be("the transcript");
        File.ReadAllText(Archived("projects/p/s1/subagents/a.jsonl")).Should().Be("a subagent");
        File.GetLastWriteTimeUtc(Archived("projects/p/s1.jsonl")).Should().Be(new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc), "the copy keeps the source's last write");
        entry.State.Should().Be(InflightStates.Archived);
        Index(first).Should().ContainSingle(e => e.EntryId == entry.EntryId && e.Status == ArchiveIndex.Events.Archived && e.Verified);

        var (later, outcome) = RemoveLater(entry);

        outcome.Should().BeOfType<RemoveOutcome.Removed>().Which.Event.Should().Be(ArchiveIndex.Events.SourceRemoved);
        File.Exists(Source("projects/p/s1.jsonl")).Should().BeFalse();
        Directory.Exists(Source("projects/p/s1")).Should().BeFalse("the companion folder the session left is removed once empty");
        Directory.Exists(Source("projects/p")).Should().BeTrue("the project folder is never removed by a session");
        File.ReadAllText(Archived("projects/p/s1.jsonl")).Should().Be("the transcript", "the archived copy stays");
        Index(later).Single(e => e.EntryId == entry.EntryId).Status.Should().Be(ArchiveIndex.Events.SourceRemoved);
        later.Book.Entries.Should().BeEmpty();
        _steps.Should().ContainInOrder(MoveSteps.QuarantineStart, nameof(ArchiveFileStep.ArchivedCopyHashed), nameof(ArchiveFileStep.Removed), MoveSteps.CloseStart, MoveSteps.Closed);
    }

    [Fact]
    public void An_archived_copy_that_changed_before_the_removal_keeps_the_source_and_marks_the_entry_damaged()
    {
        var entry = Copied(Run("r1"), Session("s2"));
        File.WriteAllText(Archived("projects/p/s2.jsonl"), "a share that lost the write");

        var (later, outcome) = RemoveLater(entry);

        outcome.Should().BeOfType<RemoveOutcome.Damaged>();
        File.ReadAllText(Source("projects/p/s2.jsonl")).Should().Be("the transcript");
        Index(later).Single(e => e.EntryId == entry.EntryId).Status.Should().Be(ArchiveIndex.Events.Damaged);
    }

    /// <summary>Coai G1: the copy read back different — removed, copied again; different again → the session is skipped AND the run
    /// stops, nothing indexed, nothing removed.</summary>
    [Fact]
    public void A_second_read_back_mismatch_skips_the_session_and_stops_the_run()
    {
        var unit = Session("s3");
        var c = Run("r1");
        var target = Archived("projects/p/s3.jsonl");
        _fault = step =>
        {
            if (step == nameof(ArchiveFileStep.FolderFlushed) && File.Exists(target))
            {
                File.AppendAllText(target, "the base kept something else");
            }
        };

        var outcome = ArchiveCopy.Copy(c, Agent, On(Layout), unit);

        outcome.Should().BeOfType<CopyOutcome.Stop>().Which.Why.Should().Contain("twice");
        File.Exists(target).Should().BeFalse("the run's own bad copy is removed");
        File.ReadAllText(Source("projects/p/s3.jsonl")).Should().Be("the transcript");
        Index(c).Should().BeEmpty();
        c.Book.Entries.Should().BeEmpty();
    }

    [Fact]
    public void A_source_the_agent_removed_mid_run_is_counted_not_failed()
    {
        var unit = Session("s4");
        _fault = step =>
        {
            if (step == MoveSteps.Intent)
            {
                File.Delete(Source("projects/p/s4.jsonl"));
            }
        };

        var c = Run("r1");
        ArchiveCopy.Copy(c, Agent, On(Layout), unit).Should().BeOfType<CopyOutcome.GoneAtSource>();

        c.Book.Entries.Should().BeEmpty();
        Index(c).Should().BeEmpty();
    }

    /// <summary>Archive plan §8b, B1: the agent resumed the session after it was archived — an append lands in the quarantined file
    /// before the check; every file is renamed back, the copies stay as a snapshot, the entry is superseded.</summary>
    [Fact]
    public void A_session_changed_during_removal_stays_whole_at_the_source_and_its_copy_is_marked_superseded()
    {
        var entry = Copied(Run("r1"), Session("s5"));
        var renames = 0;
        _fault = step =>
        {
            // At the SECOND rename the first file is under its quarantine name and its rename handle is closed (Windows).
            if (step == nameof(ArchiveFileStep.Renamed) && ++renames == 2)
            {
                File.AppendAllText(Source("projects/p/s5.jsonl") + ArchiveNames.QuarantineMark + "r2", " and more");
            }
        };

        var (later, outcome) = RemoveLater(entry);

        outcome.Should().BeOfType<RemoveOutcome.Superseded>();
        File.ReadAllText(Source("projects/p/s5.jsonl")).Should().Be("the transcript and more");
        File.ReadAllText(Source("projects/p/s5/subagents/a.jsonl")).Should().Be("a subagent", "its subagents' files stay too");
        Directory.EnumerateFiles(On(Layout), "*" + ArchiveNames.QuarantineMark + "*", SearchOption.AllDirectories).Should().BeEmpty();
        File.ReadAllText(Archived("projects/p/s5.jsonl")).Should().Be("the transcript", "the archived copy stays as a snapshot");
        Index(later).Single(e => e.EntryId == entry.EntryId).Status.Should().Be(ArchiveIndex.Events.Superseded);
    }

    /// <summary>D2 step 8, the check BEFORE the commit point: a COMPANION that changed while the files were aside sends the whole
    /// session back — the transcript is never removed on the strength of the later per-file check (that one would remove the
    /// transcript first and keep only the changed companion: a split where nothing should have gone).</summary>
    [Fact]
    public void A_companion_changed_while_aside_keeps_the_whole_session_before_the_commit_point()
    {
        var entry = Copied(Run("r1"), Session("s13"));
        var renames = 0;
        var appended = false;
        _fault = step =>
        {
            renames += step == nameof(ArchiveFileStep.Renamed) ? 1 : 0;
            // Both files aside and every rename handle closed: the check's first open is about to happen.
            if (renames == 2 && step == nameof(ArchiveFileStep.PathChecked) && !appended)
            {
                appended = true;
                File.AppendAllText(Source("projects/p/s13/subagents/a.jsonl") + ArchiveNames.QuarantineMark + "r2", " went on");
            }
        };

        var (later, outcome) = RemoveLater(entry);

        outcome.Should().BeOfType<RemoveOutcome.Superseded>();
        File.ReadAllText(Source("projects/p/s13.jsonl")).Should().Be("the transcript", "nothing of the session passed the commit point");
        File.ReadAllText(Source("projects/p/s13/subagents/a.jsonl")).Should().Be("a subagent went on");
        Index(later).Single(e => e.EntryId == entry.EntryId).Status.Should().Be(ArchiveIndex.Events.Superseded);
    }

    /// <summary>Consult 26b4a958 point C-3: removal is per UNIT. The transcript changes AFTER the commit point (an append landing in the
    /// quarantined file just before its own removal) — then not one file of the session is removed: the transcript kept, every
    /// companion kept with it, all back under their names; never a transcript at the source without its companions.</summary>
    [Fact]
    public void A_transcript_that_changed_after_the_commit_point_keeps_every_file_of_its_session()
    {
        var entry = Copied(Run("r1"), Session("s12"));
        var appended = false;
        _fault = step =>
        {
            if (step == nameof(ArchiveFileStep.ArchivedCopyHashed) && !appended)
            {
                appended = true;
                File.AppendAllText(Source("projects/p/s12.jsonl") + ArchiveNames.QuarantineMark + "r2", " and the agent went on");
            }
        };

        var (later, outcome) = RemoveLater(entry);

        outcome.Should().BeOfType<RemoveOutcome.Superseded>();
        File.ReadAllText(Source("projects/p/s12.jsonl")).Should().Be("the transcript and the agent went on");
        File.ReadAllText(Source("projects/p/s12/subagents/a.jsonl")).Should().Be("a subagent", "a companion never leaves without its transcript");
        Directory.EnumerateFiles(On(Layout), "*" + ArchiveNames.QuarantineMark + "*", SearchOption.AllDirectories).Should().BeEmpty();
        Index(later).Single(e => e.EntryId == entry.EntryId).Status.Should().Be(ArchiveIndex.Events.Superseded);
    }

    /// <summary>Risk consult 9/9.2 row 2: a file left under its quarantine name with NO in-flight entry (local state lost) is renamed
    /// back — never unlinked, since nothing says its commit point passed.</summary>
    [Fact]
    public void A_quarantined_file_without_its_in_flight_entry_is_renamed_back_never_removed()
    {
        var quarantined = Write($"{Layout}/projects/p/s6.jsonl{ArchiveNames.QuarantineMark}r9", "aside");
        Write($"{Layout}/projects/p/s7.jsonl{ArchiveNames.QuarantineMark}r9", "aside too");
        Write($"{Layout}/projects/p/s7.jsonl", "the agent made it again");
        var c = Run("r1");

        var report = ArchiveReconcile.RenameBack(c, Agent, On(Layout), ["projects/p/s6.jsonl" + ArchiveNames.QuarantineMark + "r9", "projects/p/s7.jsonl" + ArchiveNames.QuarantineMark + "r9"], ArchiveReconcileReport.Empty);

        report.RenamedBack.Should().Be(1);
        report.KeptAside.Should().Be(1);
        File.ReadAllText(Source("projects/p/s6.jsonl")).Should().Be("aside");
        File.Exists(quarantined).Should().BeFalse();
        File.ReadAllText(Source("projects/p/s7.jsonl")).Should().Be("the agent made it again");
        File.ReadAllText(Source($"projects/p/s7.jsonl{ArchiveNames.QuarantineMark}r9")).Should().Be("aside too", "both are kept, never replaced");
    }

    /// <summary>D3: a <c>removing</c> entry is resumed by the reconcile — but never removes a source whose archived copy no longer matches.</summary>
    [Fact]
    public void The_reconcile_never_removes_a_source_on_a_mismatch()
    {
        var entry = Copied(Run("r1"), Session("s8"));
        _fault = step =>
        {
            if (step == nameof(ArchiveFileStep.ArchivedCopyHashed))
            {
                throw new OperationCanceledException("killed after the commit point");
            }
        };
        FluentActions.Invoking(() => RemoveLater(entry)).Should().Throw<OperationCanceledException>();
        _fault = static _ => { };
        File.WriteAllText(Archived("projects/p/s8.jsonl"), "changed while the run was down");

        var report = ArchiveReconcile.FromInflight(Run("r3"));

        report.Resumed.Should().Be(0);
        File.ReadAllText(Source("projects/p/s8.jsonl")).Should().Be("the transcript", "the source is renamed back, never removed");
        File.Exists(Source("projects/p/s8.jsonl") + ArchiveNames.QuarantineMark + "r2").Should().BeFalse();
    }

    /// <summary>D3: a crash in phase 1 after the index line — the reconcile finds the unit indexed and moves the entry to archived.</summary>
    [Fact]
    public void A_copying_entry_the_index_holds_becomes_archived_and_one_it_does_not_is_dropped()
    {
        var unit = Session("s9");
        _fault = step =>
        {
            if (step == MoveSteps.IndexFlushed)
            {
                throw new OperationCanceledException("killed after the index flush");
            }
        };
        FluentActions.Invoking(() => ArchiveCopy.Copy(Run("r1"), Agent, On(Layout), unit)).Should().Throw<OperationCanceledException>();
        var unindexed = Session("s10");
        _fault = step =>
        {
            if (step == MoveSteps.BetweenFiles)
            {
                throw new OperationCanceledException("killed between files");
            }
        };
        FluentActions.Invoking(() => ArchiveCopy.Copy(Run("r1"), Agent, On(Layout), unindexed)).Should().Throw<OperationCanceledException>();
        _fault = static _ => { };

        var c = Run("r2");
        var report = ArchiveReconcile.FromInflight(c);

        report.Indexed.Should().Be(1);
        report.Dropped.Should().Be(1);
        c.Book.Entries.Should().ContainSingle().Which.Key.Should().Be("projects/p/s9.jsonl");
        File.Exists(Source("projects/p/s10.jsonl")).Should().BeTrue("phase 1 never touched the source");
    }

    /// <summary>D2.5: a copy a crashed run left at the name is REUSED when it holds the same bytes, never copied over.</summary>
    [Fact]
    public void A_copy_an_earlier_run_left_with_the_same_bytes_is_reused_and_other_bytes_get_a_new_name()
    {
        Write($"{Base}/{Agent}/{Month}/{Side}/projects/p/s11.jsonl", "the transcript");
        Write($"{Base}/{Agent}/{Month}/{Side}/projects/p/s11/subagents/a.jsonl", "other bytes");

        var entry = Copied(Run("r1"), Session("s11"));

        var indexed = Index(Run("r1")).Single(e => e.EntryId == entry.EntryId);
        indexed.Files.Select(f => f.Archived).Should().Equal("projects/p/s11.jsonl", "projects/p/s11/subagents/a~2.jsonl");
        File.ReadAllText(Archived("projects/p/s11/subagents/a.jsonl")).Should().Be("other bytes", "a file of the base is never replaced");
    }
}
