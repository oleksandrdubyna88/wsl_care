using FluentAssertions;

using WslCare.Core.Agents;
using WslCare.Core.Hosting;

namespace WslCare.Core.Tests.Agents;

/// <summary>
/// Plan §15r E9.S0: the catalogue's <c>archive</c> blocks — what moves of each agent, what never moves, and where the agent keeps
/// its own retention — and the layouts the one-time run of 2026-10-02 confirmed that the catalogue did not yet hold.
/// </summary>
public sealed class AgentArchiveCatalogueTests
{
    private static readonly WindowsEnvironment Windows = new(@"C:\Users\me", @"C:\Users\me\AppData\Roaming", @"C:\Users\me\AppData\Local", @"C:\ProgramData", @"C:\Users\me\AppData\Local\Temp");

    private static AgentEntry Agent(string id) => AgentCatalogue.Agents.Single(a => a.Id == id);

    [Fact]
    public void The_four_agents_whose_layout_was_confirmed_are_archivable_and_no_other()
    {
        AgentCatalogue.ArchivableIds.Should().Equal("claude-code", "codex", "gemini-cli", "antigravity");
        AgentCatalogue.Agents.Where(a => a.Archive is not null).Should().OnlyContain(a => a.Confirmed && a.Sessions != null, "an archive moves only a confirmed layout (archive plan §3)");
    }

    /// <summary>The one-time run archived 2 889 Windows Antigravity conversations from the Windows home's
    /// <c>.gemini/antigravity-cli</c>; the catalogue named no Windows layout for it (<c>windowsUnder</c> was empty).</summary>
    [Fact]
    public void Antigravity_on_windows_has_its_confirmed_layout()
    {
        var antigravity = Agent("antigravity");

        antigravity.Sessions!.WindowsUnder.Should().Be(@"%USERPROFILE%\.gemini\antigravity-cli");
        antigravity.Windows.Should().Contain([@"%USERPROFILE%\.gemini\antigravity-cli", @"%USERPROFILE%\.gemini\antigravity"]);
        AgentCatalogue.WindowsFolders(Windows).Should().Contain(@"C:\Users\me\.gemini\antigravity-cli");
    }

    [Fact]
    public void Antigravity_archives_each_cli_log_as_a_unit_of_its_own()
    {
        Agent("antigravity").Archive!.Units.Should().ContainSingle(u => u.Kind == ArchiveUnitKinds.File)
            .Which.Glob.Should().Be("log/cli-*.log");
    }

    /// <summary>§15r review M8: <c>conversations/&lt;id&gt;.db</c> is probably SQLite — its sidecars belong to the conversation,
    /// and a conversation whose <c>-wal</c> exists is left where it is until the live gate says otherwise.</summary>
    [Fact]
    public void Antigravity_sqlite_sidecars_travel_with_their_conversation_and_a_wal_keeps_it_in_place()
    {
        var antigravity = Agent("antigravity");

        antigravity.Sessions!.Companions.Should().Contain(["{dir}/{id}.db-wal", "{dir}/{id}.db-shm", "{dir}/{id}.db-journal"]);
        antigravity.Archive!.Units.Single(u => u.Kind == ArchiveUnitKinds.Session).SkipWhilePresent.Should().Equal("{dir}/{id}.db-wal");
    }

    [Fact]
    public void Every_archive_block_is_sound_and_no_unit_names_what_never_moves()
    {
        AgentCatalogue.Agents.SelectMany(AgentArchiveRules.Problems).Should().BeEmpty();
    }

    /// <summary>The companion of the rule above: a planted unit that names a never-move folder, an unknown kind and an unknown
    /// retention source are each found — a check that matched nothing would pass forever.</summary>
    [Fact]
    public void The_archive_rules_find_a_planted_unit_that_names_what_never_moves()
    {
        var antigravity = Agent("antigravity");
        var planted = antigravity with
        {
            Archive = antigravity.Archive! with
            {
                Units = [.. antigravity.Archive.Units, new ArchiveUnit(ArchiveUnitKinds.File, "presence/*.lock", []), new ArchiveUnit("folder", string.Empty, [])],
                Retention = antigravity.Archive.Retention with { Source = "guess" },
            },
        };

        AgentArchiveRules.Problems(planted).Should().HaveCount(3)
            .And.Contain(p => p.Contains("\"presence\"", StringComparison.Ordinal))
            .And.Contain(p => p.Contains("\"folder\"", StringComparison.Ordinal))
            .And.Contain(p => p.Contains("\"guess\"", StringComparison.Ordinal));
    }

    /// <summary>Plan §15q H2: memory never moves for ANY agent, in any case, at any depth — whatever a block lists.</summary>
    [Theory]
    [InlineData("projects/p/memory/notes.md")]
    [InlineData("projects/p/Memory")]
    [InlineData("MEMORY/x.jsonl")]
    public void Memory_is_never_moved_by_any_archive_block(string relative)
    {
        AgentCatalogue.Agents.Where(a => a.Archive is not null).Should().OnlyContain(a => AgentArchiveRules.IsNeverMoved(a.Archive!, relative));
    }

    [Theory]
    [InlineData("claude-code", "settings.json")]
    [InlineData("claude-code", "settings.local.json")]
    [InlineData("claude-code", "plugins/x")]
    [InlineData("codex", "state_5.sqlite")]
    [InlineData("codex", "auth.json")]
    [InlineData("codex", "session_index.jsonl")]
    [InlineData("gemini-cli", "tmp/p/logs.json")]
    [InlineData("gemini-cli", "history/x")]
    [InlineData("antigravity", "presence/c1.lock")]
    [InlineData("antigravity", "conversation_summaries.db-wal")]
    [InlineData("antigravity", "implicit/x.pb")]
    public void The_archive_plans_never_moved_column_is_in_the_catalogue(string id, string relative)
    {
        AgentArchiveRules.IsNeverMoved(Agent(id).Archive!, relative).Should().BeTrue($"archive plan §3 says {relative} of {id} is never moved");
    }

    [Fact]
    public void A_session_file_is_not_never_moved()
    {
        AgentArchiveRules.IsNeverMoved(Agent("claude-code").Archive!, "projects/p/s1.jsonl").Should().BeFalse("a positive beside the negatives: the rule is not 'everything'");
    }

    /// <summary>Plan §15r D10: Claude Code deletes its own transcripts after cleanupPeriodDays (30 by default); the others name what
    /// was checked to say they keep no retention of their own.</summary>
    [Fact]
    public void Claude_code_keeps_its_own_retention_and_the_others_say_what_was_checked()
    {
        Agent("claude-code").Archive!.Retention.Should().Be(new AgentRetention(RetentionSources.ClaudeSettings, 30, Agent("claude-code").Archive!.Retention.Checked));
        AgentCatalogue.Agents.Where(a => a.Archive is not null && a.Id != "claude-code").Should().OnlyContain(a => a.Archive!.Retention.Source == RetentionSources.None && a.Archive.Retention.Checked.Contains("NOT", StringComparison.Ordinal));
    }
}
