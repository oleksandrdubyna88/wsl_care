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
    private static JsonNode Golden(string file) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(ReleaseFiles.Root, "contracts", "golden", "head", file)))!;

    private static JsonArray Removed(JsonNode show) => show["detail"]!["actions"]![0]!["run"]!["removed"]!.AsArray();

    /// <summary>The session keys an A13 run's detail must never carry: any path of an agent's session file.</summary>
    internal static IReadOnlyList<string> SessionKeysIn(string text) =>
        [.. System.Text.RegularExpressions.Regex.Matches(text, @"[A-Za-z0-9._-]+/[A-Za-z0-9._/-]*\.jsonl").Select(m => m.Value)];

    [Fact]
    public void The_archive_run_golden_names_agents_and_counts_never_a_session()
    {
        var text = File.ReadAllText(Path.Combine(ReleaseFiles.Root, "contracts", "golden", "head", ArchiveRunGoldens.ArchiveFile));
        var show = Golden(ArchiveRunGoldens.ArchiveFile);

        show["run"]!["actions"]![0]!["id"]!.GetValue<string>().Should().Be("A13");
        Removed(show).Should().NotBeEmpty().And.OnlyContain(item => item!["kind"]!.GetValue<string>() == "agent", "per agent, as the engine records it");
        SessionKeysIn(text).Should().BeEmpty("§15q R9: root's run detail names no session");
    }

    [Fact]
    public void The_restore_run_golden_names_the_sessions_it_put_back_by_entry_id_only()
    {
        var text = File.ReadAllText(Path.Combine(ReleaseFiles.Root, "contracts", "golden", "head", ArchiveRunGoldens.RestoreFile));
        var show = Golden(ArchiveRunGoldens.RestoreFile);

        show["run"]!["actions"]![0]!["id"]!.GetValue<string>().Should().Be("A20");
        Removed(show).Should().NotBeEmpty().And.OnlyContain(item => item!["kind"]!.GetValue<string>() == "archived session" && System.Text.RegularExpressions.Regex.IsMatch(item["name"]!.GetValue<string>(), "^[0-9a-f]{16}$"));
        SessionKeysIn(text).Should().BeEmpty("an entry id, never the session's path");
    }

    [Fact]
    public void The_session_key_scan_finds_a_planted_key()
    {
        SessionKeysIn("{\"note\": \"copied projects/p/s1.jsonl\"}").Should().Equal("projects/p/s1.jsonl");
        SessionKeysIn("{\"note\": \"copied 1 session(s) (2 files, 200 bytes)\"}").Should().BeEmpty();
    }
}
