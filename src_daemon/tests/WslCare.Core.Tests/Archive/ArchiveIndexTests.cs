using System.Text;

using FluentAssertions;

using WslCare.Core.Archive;

namespace WslCare.Core.Tests.Archive;

/// <summary>
/// Plan §15r D2, D4 (coai G3, review M5) — the month index as UNTRUSTED input: readers merge an entry's events and keep the files of
/// its <c>archived</c> event; a repeated line is the same fact; a torn last line, a malformed or a hostile one is skipped and counted;
/// a line this side's key did not sign reads unverified, never invalid.
/// </summary>
public sealed class ArchiveIndexTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Hash = new('a', 64);

    private static IndexLine Line(string @event, string entryId = "0123456789abcdef", IReadOnlyList<IndexFile>? files = null) =>
        new(ArchiveIndex.SchemaVersion, @event, entryId, "claude-code", "wsl-host-distro", "projects/p/s.jsonl", "2026/09", At, "UTC", "r1",
            files ?? [], string.Empty);

    private static IReadOnlyList<IndexFile> Files => [new("projects/p/s.jsonl", "projects/p/s.jsonl", 10, Hash, At), new("projects/p/s/subagents/a.jsonl", "projects/p/s/subagents/a~2.jsonl", 3, Hash, At)];

    private static byte[] Bytes(params byte[][] lines) => [.. lines.SelectMany(l => l)];

    [Fact]
    public void Readers_merge_an_entrys_events_and_keep_its_files()
    {
        var archived = ArchiveIndex.Line(Line(ArchiveIndex.Events.Archived, files: Files), Key);
        var removed = ArchiveIndex.Line(Line(ArchiveIndex.Events.SourceRemoved), Key);

        var read = ArchiveIndex.Read(Bytes(archived, archived, removed), Key);
        var entry = ArchiveIndex.Merge(read.Records).Should().ContainSingle().Subject;

        entry.Files.Select(f => f.Archived).Should().Equal("projects/p/s.jsonl", "projects/p/s/subagents/a~2.jsonl");
        entry.Status.Should().Be(ArchiveIndex.Events.SourceRemoved, "the latest event is the status");
        entry.Verified.Should().BeTrue();
        read.Skipped.Should().Be(0);
    }

    [Fact]
    public void A_torn_malformed_or_hostile_line_is_skipped_and_counted()
    {
        var good = ArchiveIndex.Line(Line(ArchiveIndex.Events.Archived, files: Files), Key);
        var outside = ArchiveIndex.Line(Line(ArchiveIndex.Events.Archived, "1123456789abcdef", [new("../../.ssh/id_rsa", "x", 1, Hash, At)]), Key);
        var rooted = ArchiveIndex.Line(Line(ArchiveIndex.Events.Archived, "2123456789abcdef", [new("/etc/shadow", "x", 1, Hash, At)]), Key);
        var badId = ArchiveIndex.Line(Line(ArchiveIndex.Events.Archived, "NOT-HEX"), Key);
        var unknownEvent = ArchiveIndex.Line(Line("deleteEverything"), Key);
        var torn = good[..(good.Length / 2)];

        var read = ArchiveIndex.Read(Bytes(good, outside, rooted, badId, unknownEvent, Encoding.UTF8.GetBytes("not json\n"), torn), Key);

        read.Records.Should().ContainSingle().Which.Line.EntryId.Should().Be("0123456789abcdef");
        read.Skipped.Should().Be(6);
    }

    /// <summary>Review M5: a line written by anything but this side — another key, an edited field — reads unverified.</summary>
    [Fact]
    public void A_line_this_side_did_not_sign_reads_unverified()
    {
        var other = ArchiveIndex.Line(Line(ArchiveIndex.Events.Archived, files: Files), [.. Key.Select(b => (byte)(b ^ 0xFF))]);
        var edited = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(ArchiveIndex.Line(Line(ArchiveIndex.Events.Archived, "1123456789abcdef", Files), Key)).Replace("\"bytes\":10", "\"bytes\":11", StringComparison.Ordinal));

        var read = ArchiveIndex.Read(Bytes(other, edited), Key);

        read.Records.Should().HaveCount(2).And.OnlyContain(r => !r.Verified);
        ArchiveIndex.Read(Bytes(ArchiveIndex.Line(Line(ArchiveIndex.Events.Archived), Key)), []).Records.Single().Verified.Should().BeFalse("a lost key makes every line unverified, never invalid");
    }

    [Fact]
    public void An_entry_id_is_sixteen_hex_of_the_side_agent_key_and_hashes()
    {
        var id = ArchiveIndex.EntryIdOf("wsl-host-distro", "claude-code", "projects/p/s.jsonl", [Hash]);

        id.Should().MatchRegex("^[0-9a-f]{16}$");
        ArchiveIndex.EntryIdOf("wsl-host-distro", "claude-code", "projects/p/s.jsonl", [new string('b', 64)]).Should().NotBe(id);
        ArchiveIndex.EntryIdOf("windows-host", "claude-code", "projects/p/s.jsonl", [Hash]).Should().NotBe(id, "two sides never share an entry");
    }
}
