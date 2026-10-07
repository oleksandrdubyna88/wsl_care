using System.Text;

using FluentAssertions;

using WslCare.Core.Archive;

namespace WslCare.Core.Tests.Archive;

/// <summary>Plan §15r *E9.S2b own review round* — the month index's reader against lines no writer of ours produces.</summary>
public sealed partial class ArchiveIndexTests
{
    private const string WholeLine =
        "{\"v\":1,\"event\":\"archived\",\"entryId\":\"0123456789abcdef\",\"agent\":\"claude-code\",\"side\":\"wsl-host-distro\",\"key\":\"projects/p/s.jsonl\",\"month\":\"2026/09\",\"atUtc\":\"2026-10-01T12:00:00+00:00\",\"zone\":\"UTC\",\"runId\":\"r1\",\"files\":[],\"mac\":\"00\"}";

    /// <summary>Correctness review M2: a line a hostile or broken writer left with a field missing (JSON gives it null) is malformed —
    /// skipped and counted — and never crashes the reader.</summary>
    [Theory]
    [InlineData("\"mac\":\"00\"", "\"x\":0")]
    [InlineData("\"agent\":\"claude-code\",", "")]
    [InlineData("\"side\":\"wsl-host-distro\",", "")]
    [InlineData("\"key\":\"projects/p/s.jsonl\",", "")]
    [InlineData("\"month\":\"2026/09\",", "")]
    [InlineData("\"zone\":\"UTC\",", "")]
    [InlineData("\"runId\":\"r1\",", "")]
    [InlineData("\"files\":[]", "\"files\":[null]")]
    [InlineData("\"files\":[]", "\"files\":null")]
    public void A_line_with_a_missing_field_is_skipped_never_thrown(string field, string replacement)
    {
        var line = WholeLine.Replace(field, replacement, StringComparison.Ordinal);
        line.Should().NotBe(WholeLine, "the row changes the line");

        var read = ArchiveIndex.Read(Encoding.UTF8.GetBytes(line + "\n"), Key);

        read.Records.Should().BeEmpty();
        read.Skipped.Should().Be(1);
    }

    /// <summary>Correctness review M1: a damaged copy is copied again under a new name (the same entry id — the id names the session
    /// and its source hashes); readers take the files of the LATEST <c>archived</c> event, so the repaired copy is the one named.</summary>
    [Fact]
    public void An_entry_archived_again_after_damage_names_the_files_of_its_latest_archived_event()
    {
        var first = ArchiveIndex.Line(Line(ArchiveIndex.Events.Archived, files: Files), Key);
        var damaged = ArchiveIndex.Line(Line(ArchiveIndex.Events.Damaged), Key);
        IReadOnlyList<IndexFile> repaired = [new("projects/p/s.jsonl", "projects/p/s~2.jsonl", 10, Hash, At)];
        var again = ArchiveIndex.Line(Line(ArchiveIndex.Events.Archived, files: repaired), Key);

        var entry = ArchiveIndex.Merge(ArchiveIndex.Read(Bytes(first, damaged, again), Key).Records).Single();

        entry.Files.Select(f => f.Archived).Should().Equal("projects/p/s~2.jsonl");
        entry.Status.Should().Be(ArchiveIndex.Events.Archived);
    }

    /// <summary>Correctness review M5 (A): a line this side did not sign — planted, or edited — with the id of a verified entry never
    /// changes it: neither its status nor its files, and the entry stays verified.</summary>
    [Fact]
    public void An_unverified_line_never_changes_a_verified_entry()
    {
        var archived = ArchiveIndex.Line(Line(ArchiveIndex.Events.Archived, files: Files), Key);
        var planted = ArchiveIndex.Line(Line(ArchiveIndex.Events.Restored), [.. Key.Select(b => (byte)(b ^ 0xFF))]);

        var entry = ArchiveIndex.Merge(ArchiveIndex.Read(Bytes(archived, planted), Key).Records).Single();

        entry.Verified.Should().BeTrue();
        entry.Status.Should().Be(ArchiveIndex.Events.Archived);
        entry.Files.Should().HaveCount(2);
    }

    /// <summary>Correctness M7 / security M-3 (plan D4): a <c>recovered</c> line — what <c>reconcile --scan</c> writes for a copy no
    /// line named, a file someone may have planted on a shared base — is unverified even with this side's MAC, and a later status
    /// line does not make it verified.</summary>
    [Fact]
    public void A_recovered_entry_is_unverified_even_signed_by_this_side()
    {
        var recovered = ArchiveIndex.Line(Line(ArchiveIndex.Events.Recovered, files: Files), Key);
        var restored = ArchiveIndex.Line(Line(ArchiveIndex.Events.Restored), Key);

        ArchiveIndex.Merge(ArchiveIndex.Read(Bytes(recovered), Key).Records).Single().Verified.Should().BeFalse();
        ArchiveIndex.Merge(ArchiveIndex.Read(Bytes(recovered, restored), Key).Records).Single().Verified.Should().BeFalse();
    }
}
