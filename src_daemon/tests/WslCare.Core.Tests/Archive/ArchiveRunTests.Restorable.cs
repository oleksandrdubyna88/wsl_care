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
}
