using System.Text.Json.Nodes;

using FluentAssertions;

namespace WslCare.Scenarios;

/// <summary>
/// E10.S2c (plan §15s D8): what the two archive run goldens must hold — on every OS, read from the checked-in files the Linux legs
/// regenerate. An A13 run's detail names agents and counts, never a session (§15q R9: root's run detail is world-readable, the
/// sessions live in the owner's own index); an A20 run's detail names the archived sessions it put back by their ENTRY ID only.
/// </summary>
public sealed class ArchiveRunGoldenTests
{
    private static string Text(string file) => File.ReadAllText(Path.Combine(ReleaseFiles.Root, "contracts", "golden", "head", file));

    private static JsonNode Golden(string file) => JsonNode.Parse(Text(file))!;

    private static JsonArray Removed(JsonNode show) => show["detail"]!["actions"]![0]!["run"]!["removed"]!.AsArray();

    /// <summary>The session keys an A13 run's detail must never carry: any path of an agent's session or database file, or its bare
    /// name — in any script (the code round's #1: a project folder may be named in Chinese as well as in ASCII).</summary>
    internal static IReadOnlyList<string> SessionKeysIn(string text) =>
        [.. System.Text.RegularExpressions.Regex.Matches(text, @"[^\s""]+\.(?:jsonl|db)").Select(m => m.Value)];

    /// <summary>The own review's #3: the keys the archive scene really archived — every key the archive-restore golden names, and each
    /// key's file name — whatever their extension or script.</summary>
    private static IReadOnlyList<string> SceneKeys() =>
        [.. Golden("archive-restore.json")["restore"]!["sessions"]!.AsArray()
            .Select(s => (string)s!["key"]!)
            .SelectMany(key => new[] { key, key[(key.LastIndexOf('/') + 1)..] })
            .Distinct(StringComparer.Ordinal)];

    [Fact]
    public void The_run_goldens_name_none_of_the_sessions_the_scene_archived()
    {
        var keys = SceneKeys();

        keys.Should().Contain("projects/p/s1.jsonl", "the scene's keys are read (the check is not vacuous)");
        foreach (var file in new[] { ArchiveRunGoldens.ArchiveFile, ArchiveRunGoldens.RestoreFile })
        {
            var text = Text(file);
            keys.Where(key => text.Contains(key, StringComparison.Ordinal)).Should().BeEmpty(file);
        }
    }

    [Fact]
    public void The_archive_run_golden_names_agents_and_counts_never_a_session()
    {
        var show = Golden(ArchiveRunGoldens.ArchiveFile);

        show["run"]!["actions"]![0]!["id"]!.GetValue<string>().Should().Be("A13");
        Removed(show).Should().NotBeEmpty().And.OnlyContain(item => item!["kind"]!.GetValue<string>() == "agent", "per agent, as the engine records it");
        SessionKeysIn(Text(ArchiveRunGoldens.ArchiveFile)).Should().BeEmpty("§15q R9: root's run detail names no session");
    }

    [Fact]
    public void The_restore_run_golden_names_the_sessions_it_put_back_by_entry_id_only()
    {
        var show = Golden(ArchiveRunGoldens.RestoreFile);

        show["run"]!["actions"]![0]!["id"]!.GetValue<string>().Should().Be("A20");
        Removed(show).Should().NotBeEmpty().And.OnlyContain(item => item!["kind"]!.GetValue<string>() == "archived session" && System.Text.RegularExpressions.Regex.IsMatch(item["name"]!.GetValue<string>(), "^[0-9a-f]{16}$"));
        SessionKeysIn(Text(ArchiveRunGoldens.RestoreFile)).Should().BeEmpty("an entry id, never the session's path");
    }

    /// <summary>The own review's #1: the restore golden's entry ids are the synthetic ones — never the hash of this machine's side.</summary>
    [Fact]
    public void The_restore_run_golden_carries_only_synthetic_entry_ids()
    {
        var ids = System.Text.RegularExpressions.Regex.Matches(Text(ArchiveRunGoldens.RestoreFile), @"(?<![0-9a-f])[0-9a-f]{16}(?![0-9a-f])").Select(m => m.Value).Distinct(StringComparer.Ordinal);

        ids.Should().NotBeEmpty().And.BeSubsetOf(["0123456789abcdef", "fedcba9876543210", "00000000000000aa", "00000000000000bb"]);
    }

    [Fact]
    public void The_session_key_scan_finds_a_planted_key()
    {
        SessionKeysIn("{\"note\": \"copied projects/p/s1.jsonl\"}").Should().Equal("projects/p/s1.jsonl");
        SessionKeysIn("{\"note\": \"copied projects/项目/s1.jsonl and s2.jsonl\"}").Should().Equal("projects/项目/s1.jsonl", "s2.jsonl");
        SessionKeysIn("{\"note\": \"read conversations/c1.db\"}").Should().Equal("conversations/c1.db");
        SessionKeysIn("{\"note\": \"copied 1 session(s) (2 files, 200 bytes)\"}").Should().BeEmpty();
    }
}
