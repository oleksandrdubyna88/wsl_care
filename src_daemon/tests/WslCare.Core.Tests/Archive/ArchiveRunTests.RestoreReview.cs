using FluentAssertions;

using WslCare.Core.Archive;

namespace WslCare.Core.Tests.Archive;

/// <summary>Plan §15r *E9.S3 own review round* — which entries a restore takes: planted lines, repeated ids, the newest of a session,
/// what was asked and not found, an index that cannot be read.</summary>
public sealed partial class ArchiveRunTests
{
    private string SideFolderName => SideName.OfThisProcess(Core.Hosting.HostSide.Wsl);

    /// <summary>The month folder of this side copied into another month, its index lines rewritten to that month — what a hostile
    /// client of the share could plant (the MAC no longer holds, so every line is unverified).</summary>
    private void Planted(string fromMonth, string toMonth)
    {
        var from = On($"{Base}/claude-code/{fromMonth}/{SideFolderName}");
        var to = On($"{Base}/claude-code/{toMonth}/{SideFolderName}");
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, File.ReadAllText(file).Replace(fromMonth, toMonth, StringComparison.Ordinal));
        }
    }

    private ArchiveRunReport RestoreRun(RestoreRequest asked, string runId = "r3") => ArchiveRun.Run(Input(Config(), runId: runId) with { Restore = asked });

    /// <summary>S-M1: a planted month holds an entry of the same session — the restore takes the VERIFIED one only, never both.</summary>
    [Fact]
    public void A_session_restore_takes_the_verified_entry_never_one_planted_beside_it()
    {
        var source = ArchivedAndRemoved("s1");
        Planted("2026/08", "2020/01");

        var report = RestoreRun(new RestoreRequest([], "claude-code", string.Empty, "projects/p/s1.jsonl", false));

        report.Restore.Sessions.Should().ContainSingle().Which.Outcome.Should().Be(RestoreOutcomes.Restored);
        report.Restore.Refused.Should().Be(0);
        File.ReadAllText(source).Should().Be("the transcript");
    }

    /// <summary>S-M1: an entry id found in more than one month (one may be planted) restores nothing and names the months.</summary>
    [Fact]
    public void An_entry_id_found_in_two_months_restores_nothing_and_names_them()
    {
        var source = ArchivedAndRemoved("s1");
        var id = List().Entries.Single().EntryId;
        Planted("2026/08", "2020/01");

        var report = RestoreRun(new RestoreRequest([id], string.Empty, string.Empty, string.Empty, true));

        report.Restore.Restored.Should().Be(0);
        report.Restore.Sessions.Should().ContainSingle().Which.Note.Should().Contain("2020/01").And.Contain("2026/08");
        File.Exists(source).Should().BeFalse();
    }

    /// <summary>C-2: restored, resumed, archived again as a NEW entry, removed again — a restore of the session takes its NEWEST entry.</summary>
    [Fact]
    public void A_session_restore_takes_the_newest_entry_of_that_session()
    {
        var source = ArchivedAndRemoved("s1");
        RestoreRun(new RestoreRequest([], "claude-code", string.Empty, "projects/p/s1.jsonl", false)).Restore.Restored.Should().Be(1);
        File.WriteAllText(source, "the transcript, resumed");
        File.SetLastWriteTimeUtc(source, Now.AddDays(-40).UtcDateTime);
        ArchiveRun.Run(Input(Config(), runId: "r4")).Agents.Single().Copied.Should().Be(1);
        _clock.Advance(TimeSpan.FromHours(25));
        ArchiveRun.Run(Input(Config(), runId: "r5")).Agents.Single().Removed.Should().Be(1);

        var report = RestoreRun(new RestoreRequest([], "claude-code", string.Empty, "projects/p/s1.jsonl", false), "r6");

        report.Restore.Restored.Should().Be(1, string.Join("; ", report.Restore.Sessions.Select(s => s.Note)));
        File.ReadAllText(source).Should().Be("the transcript, resumed", "the newest entry, never the older snapshot");
    }

    /// <summary>C-2: two removed entries of one session name (a new session at the old name, archived and removed again) — the restore
    /// takes the NEWEST.</summary>
    [Fact]
    public void Of_two_removed_entries_of_a_session_the_restore_takes_the_newest()
    {
        var source = ArchivedAndRemoved("s1");
        Session("s1", "a later session at the same name");
        ArchiveRun.Run(Input(Config(), runId: "r4")).Agents.Single().Copied.Should().Be(1);
        _clock.Advance(TimeSpan.FromHours(25));
        ArchiveRun.Run(Input(Config(), runId: "r5")).Agents.Single().Removed.Should().Be(1);

        var report = RestoreRun(new RestoreRequest([], "claude-code", string.Empty, "projects/p/s1.jsonl", false), "r6");

        report.Restore.Restored.Should().Be(1);
        report.Restore.Sessions.Single().Note.Should().Contain("newest");
        File.ReadAllText(source).Should().Be("a later session at the same name");
    }

    /// <summary>C-3: an id asked for and not found is named, and the restore does not succeed.</summary>
    [Fact]
    public void An_entry_id_not_found_is_named_and_the_restore_does_not_succeed()
    {
        ArchivedAndRemoved("s1");
        var id = List().Entries.Single().EntryId;

        var report = RestoreRun(new RestoreRequest([id, "0123456789abcdef"], string.Empty, string.Empty, string.Empty, false));

        report.Restore.Restored.Should().Be(1);
        report.Restore.Refused.Should().Be(1);
        report.Restore.Sessions.Should().Contain(s => s.EntryId == "0123456789abcdef" && s.Outcome == RestoreOutcomes.NotFound);
    }

    /// <summary>C-3: an index that cannot be read is named — never read as "no entry has that id".</summary>
    [Fact]
    public void A_month_whose_index_cannot_be_read_is_named_and_the_restore_does_not_succeed()
    {
        ArchivedAndRemoved("s1");
        var id = List().Entries.Single().EntryId;
        var index = Directory.EnumerateFiles(On(Base), ArchiveIndex.FileName, SearchOption.AllDirectories).Single();
        File.Move(index, index + ".aside");
        Directory.CreateDirectory(index);

        var report = RestoreRun(new RestoreRequest([id], string.Empty, string.Empty, string.Empty, false));

        report.Restore.Refused.Should().BeGreaterThan(0);
        report.Restore.Sessions.Should().Contain(s => s.Outcome == RestoreOutcomes.Unreadable && s.Month == "2026/08");
    }

    /// <summary>C-6: a <c>restored.json</c> that does not read is never taken for an empty one and overwritten — the restore answers
    /// why; and the file is bounded: an entry restored more than <c>archive.restoredKeepDays</c> ago leaves it when another is added.</summary>
    [Fact]
    public void An_unreadable_restored_json_is_never_overwritten_and_old_entries_leave_it()
    {
        var state = new ArchiveState(_sandbox.Paths, _sandbox.Files);
        state.EnsureFolder();
        var path = Path.Combine(state.Folder, "restored.json");
        File.WriteAllText(path, "{ torn");

        state.AddRestored(new RestoredEntry("0123456789abcdef", "claude-code", "projects/p/s.jsonl", "2026/08") { RestoredAtUtc = Now }, Now).Should().NotBeEmpty();
        File.ReadAllText(path).Should().Be("{ torn");

        File.WriteAllText(path, "{\"v\":1,\"entries\":[]}");
        state.AddRestored(new RestoredEntry("0123456789abcdef", "claude-code", "projects/p/a.jsonl", "2026/08") { RestoredAtUtc = Now }, Now).Should().BeEmpty();
        var later = Now.AddDays(181);
        state.AddRestored(new RestoredEntry("1123456789abcdef", "claude-code", "projects/p/b.jsonl", "2026/08") { RestoredAtUtc = later }, later).Should().BeEmpty();

        state.Restored().Select(e => e.Key).Should().Equal(["projects/p/b.jsonl"], "the entry restored 181 days ago left it");
    }

    /// <summary>C-5: an identical restored session archived again as an EVENT adds nothing to <c>summary.json</c> — the session is
    /// already counted in its own month, never a second time under the month of its restore.</summary>
    [Fact]
    public void An_event_only_re_archive_is_not_counted_again_in_the_summary()
    {
        var source = ArchivedAndRemoved("s1");
        RestoreRun(new RestoreRequest([], "claude-code", string.Empty, "projects/p/s1.jsonl", false)).Restore.Restored.Should().Be(1);
        File.SetLastWriteTimeUtc(source, Now.AddDays(-40).UtcDateTime);
        var before = new ArchiveState(_sandbox.Paths, _sandbox.Files).Summary().Months;

        ArchiveRun.Run(Input(Config(), runId: "r4")).Agents.Single().Copied.Should().Be(1);

        new ArchiveState(_sandbox.Paths, _sandbox.Files).Summary().Months.Should().BeEquivalentTo(before);
    }
}
