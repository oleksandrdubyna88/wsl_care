using System.Text;

using FluentAssertions;

using WslCare.Core.Archive;
using WslCare.Core.Config;
using WslCare.Core.Files;

namespace WslCare.Core.Tests.Archive;

/// <summary>The S4 code round, finding 4: A20's list must stay inside the child's answer cap however large the archive grows —
/// <c>archive list --restorable</c> answers only the VERIFIED entries removed at their source, newest first, at most
/// <c>archive.maxRestoreEntries</c>, and says how many it left out.</summary>
public sealed partial class ArchiveRunTests
{
    private EffectiveConfig RestorableConfig(int most) =>
        ConfigLoader.Load([
            Defaults(),
            (new ConfigLayerFile(ConfigLayer.Machine, "/etc/wsl-care/config.json"), new FileReadResult.Content(Encoding.UTF8.GetBytes($$"""{ "archive": { "maxRestoreEntries": {{most}} } }"""))),
            (new ConfigLayerFile(ConfigLayer.User, "user.json"), new FileReadResult.Content(Encoding.UTF8.GetBytes($$"""{ "archive": { "baseFolder": "{{On(Base).Replace("\\", "\\\\", StringComparison.Ordinal)}}" } }"""))),
        ]).Config;

    [Fact]
    public void A_restorable_list_answers_only_removed_verified_entries_at_most_the_restore_cap_and_counts_the_rest()
    {
        foreach (var id in new[] { "s1", "s2", "s3" })
        {
            Session(id);
        }

        ArchiveRun.Run(Input(Config())).Agents.Single().Copied.Should().Be(3);
        _clock.Advance(TimeSpan.FromHours(25));
        Session("s4");
        var second = ArchiveRun.Run(Input(Config(), runId: "r2")).Agents.Single();
        second.Removed.Should().Be(3);
        second.Copied.Should().Be(1, "s4 is archived, not yet removed");

        var listed = ArchiveList.List(Input(RestorableConfig(2)), new ArchiveListRequest(string.Empty, string.Empty, string.Empty) { Restorable = true });

        listed.Entries.Should().HaveCount(2);
        listed.Entries.Should().OnlyContain(e => e.Status == ArchiveIndex.Events.SourceRemoved && e.Verified);
        listed.Omitted.Should().Be(1);
        ArchiveList.List(Input(RestorableConfig(2)), new ArchiveListRequest(string.Empty, string.Empty, string.Empty)).Entries.Should().HaveCount(4, "a plain list still lists every entry");
    }

    /// <summary>The E10.S0 own review, finding 1: a restorable list asked for its ENTRIES answers exactly those still restorable —
    /// an entry older than the newest <c>archive.maxRestoreEntries</c> included — and leaves nothing out on their account.</summary>
    [Fact]
    public void A_restorable_list_asked_for_its_entries_answers_them_past_the_newest_window()
    {
        foreach (var id in new[] { "s1", "s2", "s3" })
        {
            Session(id);
            ArchiveRun.Run(Input(Config(), runId: $"r-{id}")).Agents.Single().Copied.Should().Be(1);
            _clock.Advance(TimeSpan.FromHours(25));
        }

        ArchiveRun.Run(Input(Config(), runId: "r-remove"));
        var all = ArchiveList.List(Input(Config()), new ArchiveListRequest(string.Empty, string.Empty, string.Empty) { Restorable = true }).Entries;
        all.Should().HaveCount(3, "each run removed the one copied the run before");
        var oldest = all.MinBy(e => e.ArchivedAtUtc)!.EntryId;

        var window = ArchiveList.List(Input(RestorableConfig(1)), new ArchiveListRequest(string.Empty, string.Empty, string.Empty) { Restorable = true });
        var asked = ArchiveList.List(Input(RestorableConfig(1)), new ArchiveListRequest(string.Empty, string.Empty, string.Empty) { Restorable = true, EntryIds = [oldest] });

        window.Entries.Should().NotContain(e => e.EntryId == oldest, "the plain window keeps the newest one only");
        asked.Entries.Should().ContainSingle().Which.EntryId.Should().Be(oldest);
        asked.Omitted.Should().Be(0);
    }

    /// <summary>Plan §15s D6, the plan round's finding 1: the extension's Archive page counts a selection against the ceiling ONE restore
    /// takes, so every list answer carries the effective <c>archive.maxRestoreEntries</c> — a plain list, a restorable one, and one
    /// that stopped before reading (no base) — never 0.</summary>
    [Fact]
    public void Every_list_answer_carries_the_restore_ceiling_in_force()
    {
        Session("s1");
        ArchiveRun.Run(Input(Config())).Agents.Single().Copied.Should().Be(1);

        var plain = ArchiveList.List(Input(RestorableConfig(7)), new ArchiveListRequest(string.Empty, string.Empty, string.Empty));
        var restorable = ArchiveList.List(Input(RestorableConfig(7)), new ArchiveListRequest(string.Empty, string.Empty, string.Empty) { Restorable = true });
        var noBase = ArchiveList.List(Input(ConfigLoaderDefaults()), new ArchiveListRequest(string.Empty, string.Empty, string.Empty));

        plain.RestoreCeiling.Should().Be(7);
        restorable.RestoreCeiling.Should().Be(7);
        noBase.Outcome.Should().Be(RunOutcomes.NoBase);
        noBase.RestoreCeiling.Should().Be(ConfigLoaderDefaults().Int(ConfigKeys.Archive.MaxRestoreEntries), "the default the daemon ships");
    }
}
