using System.Text;

using FluentAssertions;

using WslCare.Core.Agents;
using WslCare.Core.Archive;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Archive;

/// <summary>
/// Plan §15r D2.1–D2.2, D10 (E9.S1) — the archive's selection over the distro's layout in a sandbox (every leg): one session is one
/// unit under the month of its newest file, companions included; due at the effective age, oldest first; kept in place — with its
/// rule — when open, when Claude Code works in its project, when a database's <c>-wal</c> exists, when a name cannot be held on
/// NTFS, when it names what never moves, when not every file of it was seen. Never selected: what the layout does not name.
/// </summary>
public sealed class SelectionTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly LinuxSandbox _sandbox = new("archive-selection");

    public void Dispose() => _sandbox.Dispose();

    private static (ConfigLayerFile, FileReadResult) Defaults() => (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults()));

    private static EffectiveConfig Config(string user = "{}") =>
        ConfigLoader.Load([Defaults(), (new ConfigLayerFile(ConfigLayer.Machine, "/etc/wsl-care/config.json"), new FileReadResult.Missing()), (new ConfigLayerFile(ConfigLayer.User, "user.json"), new FileReadResult.Content(Encoding.UTF8.GetBytes(user)))]).Config;

    private string File(string distro, DateTimeOffset written, int bytes = 10)
    {
        var path = _sandbox.Sized(distro, bytes, written);
        return path;
    }

    private SelectionInput Input(EffectiveConfig? config = null, InUseView? inUse = null, TimeZoneInfo? zone = null, IReadOnlyDictionary<string, string>? environment = null) =>
        new(_sandbox.Paths, _sandbox.Files, config ?? Config(), Now, zone ?? TimeZoneInfo.Utc, inUse ?? InUseView.Complete(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.OrdinalIgnoreCase)), name => environment?.GetValueOrDefault(name));

    private AgentSelection Claude(SelectionInput? input = null) => Selection.Select(input ?? Input()).Single(s => s.Entry.Id == "claude-code");

    private AgentSelection Antigravity() => Selection.Select(Input()).Single(s => s.Entry.Id == "antigravity");

    private static DateTimeOffset DaysAgo(double days) => Now.AddDays(-days);

    [Fact]
    public void A_session_moves_as_one_unit_under_the_month_of_its_newest_file()
    {
        File("/home/me/.claude/projects/p/s1.jsonl", new DateTimeOffset(2026, 8, 31, 22, 0, 0, TimeSpan.Zero));
        File("/home/me/.claude/projects/p/s1/subagents/agent-a.jsonl", new DateTimeOffset(2026, 8, 31, 23, 30, 0, TimeSpan.Zero));
        File("/home/me/.claude/file-history/s1/v1", new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero));

        var unit = Claude().Due.Should().ContainSingle().Subject;

        unit.Key.Should().Be("projects/p/s1.jsonl");
        unit.Files.Select(f => f.Relative).Should().BeEquivalentTo(["projects/p/s1.jsonl", "projects/p/s1/subagents/agent-a.jsonl", "file-history/s1/v1"]);
        unit.NewestWriteUtc.Should().Be(new DateTimeOffset(2026, 8, 31, 23, 30, 0, TimeSpan.Zero));
        unit.Month.Should().Be("2026/08");
    }

    /// <summary>§8a: the month is the side's local month of the newest file — 23:30 UTC on the 31st is already September at +02:00.</summary>
    [Fact]
    public void The_month_is_the_newest_files_month_in_the_sides_time_zone()
    {
        File("/home/me/.claude/projects/p/s1.jsonl", new DateTimeOffset(2026, 8, 31, 23, 30, 0, TimeSpan.Zero));
        var plusTwo = TimeZoneInfo.CreateCustomTimeZone("test+2", TimeSpan.FromHours(2), "test+2", "test+2");

        Claude(Input(zone: plusTwo)).Due.Single().Month.Should().Be("2026/09");
        Claude(Input(zone: TimeZoneInfo.Utc)).Due.Single().Month.Should().Be("2026/08");
    }

    [Fact]
    public void A_companion_younger_than_the_limit_keeps_the_whole_session()
    {
        File("/home/me/.claude/projects/p/s1.jsonl", DaysAgo(40));
        File("/home/me/.claude/projects/p/s1/subagents/agent-a.jsonl", DaysAgo(2));

        var claude = Claude();

        claude.Due.Should().BeEmpty("the newest file of the session is 2 days old");
        claude.Younger.Should().Be(1);
    }

    [Fact]
    public void Due_sessions_come_oldest_first()
    {
        File("/home/me/.claude/projects/p/b.jsonl", DaysAgo(20));
        File("/home/me/.claude/projects/p/a.jsonl", DaysAgo(25));
        File("/home/me/.claude/projects/q/c.jsonl", DaysAgo(16));

        Claude().Due.Select(u => u.Key).Should().Equal("projects/p/a.jsonl", "projects/p/b.jsonl", "projects/q/c.jsonl");
    }

    /// <summary>Plan §15q H2: a session named <c>memory.jsonl</c> names the companion <c>projects/p/memory</c> — the agent's memory —
    /// so the WHOLE session stays; and a memory folder at the project level is never even listed.</summary>
    [Fact]
    public void A_session_named_memory_is_refused_whole_and_memory_is_never_selected()
    {
        File("/home/me/.claude/projects/p/memory.jsonl", DaysAgo(40));
        File("/home/me/.claude/projects/p/memory/MEMORY.md", DaysAgo(40));
        File("/home/me/.claude/projects/memory/x.jsonl", DaysAgo(40));

        var claude = Claude();

        claude.Due.Should().BeEmpty();
        claude.Skipped.Should().ContainSingle().Which.Should().Match<UnitFound>(u => u.Key == "projects/p/memory.jsonl" && u.SkipRule == SkipRule.NeverMoved);
        claude.Skipped.SelectMany(u => u.Files).Should().NotContain(f => f.Relative.Contains("MEMORY", StringComparison.Ordinal));
    }

    [Fact]
    public void A_companion_folder_holding_a_folder_the_walk_never_enters_keeps_the_session_as_not_whole()
    {
        File("/home/me/.claude/projects/p/s1.jsonl", DaysAgo(40));
        File("/home/me/.claude/projects/p/s1/memory/x.md", DaysAgo(40));

        Claude().Skipped.Should().ContainSingle().Which.SkipRule.Should().Be(SkipRule.NotWhole);
    }

    [Fact]
    public void A_file_held_open_keeps_its_whole_session_at_the_source()
    {
        File("/home/me/.claude/projects/p/s1.jsonl", DaysAgo(40));
        File("/home/me/.claude/projects/p/s1/tool-results/t.txt", DaysAgo(40));
        var open = InUseView.Complete(new HashSet<string>(["/home/me/.claude/projects/p/s1/tool-results/t.txt"], StringComparer.Ordinal), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Claude(Input(inUse: open)).Skipped.Should().ContainSingle().Which.SkipRule.Should().Be(SkipRule.InUse);
    }

    [Fact]
    public void A_session_of_a_project_claude_code_works_in_stays()
    {
        File("/home/me/.claude/projects/-home-me-git-x/s1.jsonl", DaysAgo(40));
        File("/home/me/.claude/projects/-home-me-git-y/s2.jsonl", DaysAgo(40));
        var working = InUseView.Complete(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>([ArchiveNames.ClaudeProjectOf("/home/me/git/x")], StringComparer.OrdinalIgnoreCase));

        var claude = Claude(Input(inUse: working));

        claude.Skipped.Should().ContainSingle().Which.Should().Match<UnitFound>(u => u.SkipRule == SkipRule.AgentWorkingHere && u.Key.Contains("git-x"));
        claude.Due.Should().ContainSingle().Which.Key.Should().Contain("git-y");
    }

    [Fact]
    public void An_antigravity_conversation_moves_with_its_sidecars_and_stays_while_its_wal_exists()
    {
        File("/home/me/.gemini/antigravity-cli/conversations/c1.db", DaysAgo(40));
        File("/home/me/.gemini/antigravity-cli/conversations/c1.db-shm", DaysAgo(40));
        File("/home/me/.gemini/antigravity-cli/brain/c1/plan.md", DaysAgo(40));
        File("/home/me/.gemini/antigravity-cli/conversations/c2.db", DaysAgo(40));
        File("/home/me/.gemini/antigravity-cli/conversations/c2.db-wal", DaysAgo(40));

        var antigravity = Antigravity();

        antigravity.Due.Single(u => u.Key == "conversations/c1.db").Files.Select(f => f.Relative)
            .Should().BeEquivalentTo(["conversations/c1.db", "conversations/c1.db-shm", "brain/c1/plan.md"]);
        antigravity.Skipped.Should().ContainSingle().Which.Should().Match<UnitFound>(u => u.Key == "conversations/c2.db" && u.SkipRule == SkipRule.MayBeOpen);
    }

    [Fact]
    public void An_antigravity_cli_log_is_a_unit_of_its_own()
    {
        File("/home/me/.gemini/antigravity-cli/log/cli-20260901.log", DaysAgo(30));
        File("/home/me/.gemini/antigravity-cli/log/cli-20261005.log", DaysAgo(1));

        var antigravity = Antigravity();

        antigravity.Due.Should().ContainSingle().Which.Should().Match<UnitFound>(u => u.Kind == ArchiveUnitKinds.File && u.Key == "log/cli-20260901.log" && u.Files.Count == 1);
        antigravity.Younger.Should().Be(1);
    }

    [Fact]
    public void Two_names_of_a_session_that_differ_by_case_only_refuse_it()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a case-sensitive filesystem holds the two names: the Linux legs");
        File("/home/me/.claude/projects/p/s1.jsonl", DaysAgo(40));
        File("/home/me/.claude/projects/p/s1/X.md", DaysAgo(40));
        File("/home/me/.claude/projects/p/s1/x.md", DaysAgo(40));

        Claude().Skipped.Should().ContainSingle().Which.Should().Match<UnitFound>(u => u.SkipRule == SkipRule.Name && u.Skip.Contains("differ by case"));
    }

    [Fact]
    public void A_name_ntfs_refuses_refuses_its_session_with_the_reason()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "only a Linux folder holds a name with a colon: the Linux legs");
        File("/home/me/.claude/projects/p/s1.jsonl", DaysAgo(40));
        File("/home/me/.claude/projects/p/s1/a:b.txt", DaysAgo(40));

        Claude().Skipped.Should().ContainSingle().Which.Skip.Should().Contain("a:b.txt").And.Contain("NTFS");
    }

    [Theory]
    [InlineData("a\uFFFDb", "UTF-8")]
    [InlineData("con.txt", "reserves")]
    [InlineData("trailing.", "dot")]
    [InlineData("pi|pe", "NTFS refuses")]
    public void A_name_the_archive_cannot_hold_is_named_with_why(string name, string says)
    {
        ArchiveNames.Problem(name).Should().Contain(says);
        ArchiveNames.Problem("session-1.jsonl").Should().BeEmpty("a positive beside the refusals");
    }

    /// <summary>A surrogate PAIR is valid UTF-16 (one character outside the basic plane — an emoji, a rare CJK ideograph) and NTFS
    /// holds it; only a LONE surrogate is a name the distro's bytes could not decode.</summary>
    [Fact]
    public void A_name_with_a_character_outside_the_basic_plane_is_held_and_a_lone_surrogate_is_not()
    {
        ArchiveNames.Problem("notes-\U0001F600.md").Should().BeEmpty("a surrogate pair is one valid character");
        ArchiveNames.Problem("bad-\uD83D.md").Should().Contain("UTF-8");
    }

    [Fact]
    public void Claude_names_a_project_after_its_working_directory()
    {
        ArchiveNames.ClaudeProjectOf(@"D:\rsd\ClaudeRag").Should().Be("D--rsd-ClaudeRag", "observed as d--rsd-ClaudeRag, compared case-insensitively");
        ArchiveNames.ClaudeProjectOf("/home/me/git/x.y").Should().Be("-home-me-git-x-y");
    }

    /// <summary>§15r D10, review M7: the measured retention SHORTENS the age — a Claude Code kept 10 days is archived from day 2
    /// (10 − 7 margin − 1 removal day), never from day 14.</summary>
    [Fact]
    public void The_measured_retention_shortens_the_effective_age()
    {
        _sandbox.Write("/home/me/.claude/settings.json", """{ "cleanupPeriodDays": 10 }""");
        File("/home/me/.claude/projects/p/s1.jsonl", DaysAgo(3));

        var claude = Claude();

        claude.Retention.Should().BeOfType<RetentionFound.Known>().Which.Days.Should().Be(10);
        claude.EffectiveAgeDays.Should().Be(2);
        claude.Due.Should().ContainSingle();
    }

    [Fact]
    public void Managed_settings_win_over_the_users_and_claude_config_dir_moves_the_users()
    {
        _sandbox.Write(AgentRetentionReader.LinuxManaged, """{ "cleanupPeriodDays": 20 }""");
        _sandbox.Write("/home/me/.claude/settings.json", """{ "cleanupPeriodDays": 10 }""");
        var moved = _sandbox.Write("/srv/claude-home/settings.json", """{ "cleanupPeriodDays": 9 }""");

        Claude().Retention.Should().BeOfType<RetentionFound.Known>().Which.Days.Should().Be(20, "an administrator's managed setting wins");
        System.IO.File.Delete(_sandbox.Paths.DistroPath(AgentRetentionReader.LinuxManaged));
        Claude().Retention.Should().BeOfType<RetentionFound.Known>().Which.Days.Should().Be(10);
        Claude(Input(environment: new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = "/srv/claude-home" })).Retention.Should().BeOfType<RetentionFound.Known>().Which.Days.Should().Be(9);
        moved.Should().NotBeEmpty();
    }

    [Fact]
    public void A_retention_of_zero_is_a_loud_warning_and_the_age_its_least()
    {
        _sandbox.Write("/home/me/.claude/settings.json", """{ "cleanupPeriodDays": 0 }""");

        var claude = Claude();

        claude.EffectiveAgeDays.Should().Be(1);
        claude.Retention.Warnings.Should().ContainSingle().Which.Should().Contain("keeps NO session");
    }

    [Fact]
    public void An_unreadable_setting_is_said_and_the_documented_default_applies()
    {
        _sandbox.Write("/home/me/.claude/settings.json", "not json");

        var claude = Claude();

        claude.Retention.Should().BeOfType<RetentionFound.Known>().Which.Days.Should().Be(30);
        claude.EffectiveAgeDays.Should().Be(14);
        claude.Retention.Warnings.Should().ContainSingle().Which.Should().Contain("not JSON");
    }

    [Fact]
    public void Files_an_interrupted_removal_quarantined_are_counted()
    {
        File("/home/me/.claude/projects/p/s1.jsonl.wsl-care-q-20261001T000000Z-1", DaysAgo(5));
        File("/home/me/.claude/projects/p/s2.jsonl", DaysAgo(40));
        File("/home/me/.claude/projects/p/s2/a.txt.wsl-care-q-20261001T000000Z-1", DaysAgo(40));

        Claude().Quarantined.Should().Be(2);
    }

    [Fact]
    public void Only_the_agents_of_archive_agents_are_selected()
    {
        File("/home/me/.claude/projects/p/s1.jsonl", DaysAgo(40));
        File("/home/me/.codex/sessions/2026/08/01/rollout-1.jsonl", DaysAgo(40));

        var selected = Selection.Select(Input(Config("""{ "archive": { "agents": ["codex"] } }""")));

        selected.Should().ContainSingle().Which.Entry.Id.Should().Be("codex");
        selected[0].Due.Should().ContainSingle();
    }

    private static string Manual(string name, string folder, string glob) =>
        $$"""{ "cli": "/home/me/.local/bin/{{name}}", "side": "wsl", "name": "{{name}}", "dataFolders": ["{{folder}}"], "sessionGlob": "{{glob}}" }""";

    /// <summary>E9.S0 review round decision (b): a manual agent that names its sessions may be archived — off by default, on when
    /// <c>archive.agents</c> lists <c>manual:&lt;name&gt;</c> — by its own glob under its first data folder, <c>memory</c> never.</summary>
    [Fact]
    public void A_manual_agent_with_a_session_glob_is_archived_only_when_archive_agents_names_it()
    {
        File("/home/me/.mycli/sessions/a.log", DaysAgo(40));
        File("/home/me/.mycli/memory/m.log", DaysAgo(40));
        var extra = Manual("mycli", "/home/me/.mycli", "sessions/*.log");

        var off = Selection.Select(Input(Config($$"""{ "aiAgents": { "extra": [{{extra}}] } }""")));
        var on = Selection.Select(Input(Config($$"""{ "aiAgents": { "extra": [{{extra}}] }, "archive": { "agents": ["manual:mycli"] } }""")));

        off.Should().NotContain(s => s.Entry.Id == "manual:mycli", "a manual agent is never archived by default");
        var mine = on.Should().ContainSingle().Subject;
        mine.Entry.Id.Should().Be("manual:mycli");
        mine.Due.Should().ContainSingle().Which.Key.Should().Be("sessions/a.log");
        mine.Retention.Should().BeOfType<RetentionFound.Unknown>("a manual agent's own deletion is not known");
    }

    [Fact]
    public void A_listed_manual_agent_without_a_glob_or_refused_by_its_folder_rules_is_said_and_never_listed()
    {
        File("/home/me/.npm/sessions/a.log", DaysAgo(40));
        var config = Config($$"""{ "aiAgents": { "extra": [{{Manual("noglob", "/home/me/.noglob", "")}}, {{Manual("npm-ish", "/home/me/.npm", "sessions/*.log")}}] }, "archive": { "agents": ["manual:noglob", "manual:npm-ish", "manual:gone"] } }""");

        var selected = Selection.Select(Input(config));

        selected.Select(s => s.Entry.Id).Should().Equal("manual:noglob", "manual:npm-ish", "manual:gone");
        selected.Should().OnlyContain(s => s.Due.Count == 0 && s.Note.Length > 0);
        selected[1].Note.Should().Contain("A8's cleanup folder");
    }

    /// <summary>The selection is read-only: no file anywhere in the sandbox was created, changed or removed.</summary>
    [Fact]
    public void The_selection_writes_nothing()
    {
        File("/home/me/.claude/projects/p/s1.jsonl", DaysAgo(40));
        File("/home/me/.gemini/antigravity-cli/conversations/c1.db", DaysAgo(40));
        var before = Snapshot();

        _ = Selection.Select(Input());

        Snapshot().Should().Equal(before);
    }

    private IReadOnlyList<string> Snapshot() =>
        [.. Directory.EnumerateFileSystemEntries(_sandbox.Root.Path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(p => $"{p}|{(System.IO.File.Exists(p) ? new FileInfo(p).Length + "|" + System.IO.File.GetLastWriteTimeUtc(p).Ticks : "dir")}")];

    /// <summary>The never-move property over random trees (§15r E9.S1): whatever a home holds, every file of a due unit is named by
    /// its agent's layout — the unit's file, or under one of its companions — and none lies at or under a name that never moves.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Nothing_outside_a_sessions_definition_is_ever_selected(int seed)
    {
        var random = new Random(seed);
        string[] names = ["memory", "Memory", "MEMORY", "settings.json", "settings.local.json", "plugins", "auth.json", "state_5.sqlite", "session_index.jsonl", "presence", "conversation_summaries.db", "implicit", "s1", "s2", "x"];
        string[] roots = ["/home/me/.claude/projects/p", "/home/me/.claude", "/home/me/.claude/projects", "/home/me/.codex/sessions/2026/08/01", "/home/me/.codex", "/home/me/.gemini/tmp/q/chats", "/home/me/.gemini/antigravity-cli/conversations", "/home/me/.gemini/antigravity-cli"];
        string[] extensions = [".jsonl", ".db", ".json", ".md", ".lock", string.Empty];
        for (var i = 0; i < 60; i++)
        {
            var depth = random.Next(0, 3);
            var path = roots[random.Next(roots.Length)] + string.Concat(Enumerable.Range(0, depth).Select(_ => "/" + names[random.Next(names.Length)]));
            Plant(path + "/" + (random.Next(3) == 0 ? "rollout-" : string.Empty) + names[random.Next(names.Length)] + extensions[random.Next(extensions.Length)]);
        }

        foreach (var agent in Selection.Select(Input()))
        {
            foreach (var file in agent.Due.SelectMany(u => u.Files))
            {
                AgentArchiveRules.IsNeverMoved(agent.Entry.Archive!, file.Relative).Should().BeFalse($"{agent.Entry.Id} took {file.Relative}");
                file.Relative.Split('/').Should().NotContain(s => s.Equals("memory", StringComparison.OrdinalIgnoreCase));
            }

            foreach (var unit in agent.Due)
            {
                unit.Files.Should().OnlyContain(f => f.Relative == unit.Key || IsCompanion(agent.Entry, unit.Key, f.Relative), $"{agent.Entry.Id}: every file of {unit.Key} is its own or a companion's");
            }
        }
    }

    /// <summary>A random file — skipped when an earlier one already took its name as a folder, or the reverse (or, on a case-blind
    /// filesystem, a name that differs by case only).</summary>
    private void Plant(string distro)
    {
        try
        {
            File(distro, DaysAgo(40));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A tree that cannot hold this name holds the others; the property is about what it does hold.
        }
    }

    private static bool IsCompanion(AgentEntry entry, string key, string relative)
    {
        var dir = key.Contains('/', StringComparison.Ordinal) ? key[..key.LastIndexOf('/')] : string.Empty;
        var id = Path.GetFileNameWithoutExtension(key);
        return entry.Sessions!.Companions
            .Select(c => c.Replace("{dir}", dir, StringComparison.Ordinal).Replace("{id}", id, StringComparison.Ordinal).TrimStart('/'))
            .Any(c => relative == c || relative.StartsWith(c + "/", StringComparison.Ordinal));
    }
}
