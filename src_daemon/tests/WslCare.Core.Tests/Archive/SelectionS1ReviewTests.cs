using System.Text;

using FluentAssertions;

using WslCare.Core.Agents;
using WslCare.Core.Archive;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Archive;

/// <summary>
/// The E9.S1 review round (plan §15r *E9.S1 review round*), the selection's half: a session whose id is empty or a dot name never
/// expands its companions to the folder around it (security M1); an open-file scan that was cut or never ran keeps every due
/// unit (correctness M1); Claude Code's sessions are read only where its retention is read (M2); a companion FILE that cannot be
/// read keeps its unit (m2); a whole session under its quarantine names is counted (m3); memory is never selected even by a glob
/// that reaches it, manual agents included (m5); the agent's own retention is a closed answer (m8); the listing has only the
/// time it was given, companions included (m1).
/// </summary>
public sealed class SelectionS1ReviewTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly LinuxSandbox _sandbox = new("selection-s1-review");

    public void Dispose() => _sandbox.Dispose();

    private static (ConfigLayerFile, FileReadResult) Defaults() => (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults()));

    private static EffectiveConfig Config(string user = "{}") =>
        ConfigLoader.Load([Defaults(), (new ConfigLayerFile(ConfigLayer.Machine, "/etc/wsl-care/config.json"), new FileReadResult.Missing()), (new ConfigLayerFile(ConfigLayer.User, "user.json"), new FileReadResult.Content(Encoding.UTF8.GetBytes(user)))]).Config;

    private void File(string distro, int days = 40) => _sandbox.Sized(distro, 10, Now.AddDays(-days));

    private SelectionInput Input(EffectiveConfig? config = null, InUseView? inUse = null, IFileSystem? files = null, Func<string, string?>? environment = null) =>
        new(_sandbox.Paths, files ?? _sandbox.Files, config ?? Config(), Now, TimeZoneInfo.Utc, inUse ?? InUseView.Complete(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.OrdinalIgnoreCase)), environment ?? (_ => null));

    private static AgentSelection Of(IReadOnlyList<AgentSelection> selected, string id) => selected.Single(s => s.Entry.Id == id);

    /// <summary>Security M1: <c>.jsonl</c> matches <c>*.jsonl</c>, and its id is empty — <c>{dir}/{id}</c> would be the project folder
    /// and <c>file-history/{id}</c> all of file-history; <c>..jsonl</c>'s id is <c>.</c>, <c>...jsonl</c>'s <c>..</c> (the agent's
    /// folder itself). Refused by name; nothing outside them is ever listed as theirs.</summary>
    [Fact]
    public void A_session_whose_id_is_empty_or_a_dot_name_is_refused_and_its_companions_are_never_listed()
    {
        File("/home/me/.claude/projects/p/.jsonl");
        File("/home/me/.claude/projects/p/..jsonl");
        File("/home/me/.claude/projects/p/...jsonl");
        File("/home/me/.claude/.credentials.json");
        File("/home/me/.claude/projects/p/other/x.txt");
        File("/home/me/.gemini/antigravity-cli/conversations/.db");
        File("/home/me/.gemini/antigravity-cli/brain/other/b.md");

        var selected = Selection.Select(Input());
        var claude = Of(selected, "claude-code");
        var antigravity = Of(selected, "antigravity");

        claude.Due.Should().BeEmpty();
        claude.Skipped.Should().HaveCount(3).And.OnlyContain(u => u.SkipRule == SkipRule.Name);
        claude.Skipped.SelectMany(u => u.Files).Should().OnlyContain(f => f.Relative == "projects/p/.jsonl" || f.Relative == "projects/p/..jsonl" || f.Relative == "projects/p/...jsonl");
        antigravity.Due.Should().BeEmpty();
        antigravity.Skipped.SelectMany(u => u.Files).Should().ContainSingle().Which.Relative.Should().Be("conversations/.db");
    }

    /// <summary>Correctness M1: a cut open-file scan, or one that never ran (Windows until E9.S5), saw nothing of what it did not
    /// reach — every due unit is kept, by the in-use rule, with the reason.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_open_file_scan_that_was_cut_or_never_ran_keeps_every_due_unit(bool cut)
    {
        File("/home/me/.claude/projects/p/s1.jsonl");
        var none = new HashSet<string>(StringComparer.Ordinal);
        var view = cut ? InUseView.Cut(none, new HashSet<string>(StringComparer.OrdinalIgnoreCase), "the open-file scan stopped at its 20 s ceiling") : InUseView.NotChecked("not on Windows yet");

        var claude = Of(Selection.Select(Input(inUse: view)), "claude-code");

        claude.Due.Should().BeEmpty();
        claude.Skipped.Should().ContainSingle().Which.Should().Match<UnitFound>(u => u.SkipRule == SkipRule.InUse && u.Skip.Contains(cut ? "cut" : "not checked"));
    }

    /// <summary>Correctness M2: the retention is read from <c>CLAUDE_CONFIG_DIR</c> when it is set — and then the sessions live there
    /// too, not under the catalogue's <c>~/.claude</c> (which the protected roots and the walk rules are). Claude Code is answered
    /// with a note and nothing is listed while the variable names another folder.</summary>
    [Fact]
    public void Claude_code_is_not_listed_while_claude_config_dir_names_another_folder()
    {
        File("/home/me/.claude/projects/p/s1.jsonl");
        var elsewhere = Of(Selection.Select(Input(environment: n => n == "CLAUDE_CONFIG_DIR" ? "/home/me/other-claude" : null)), "claude-code");
        var same = Of(Selection.Select(Input(environment: n => n == "CLAUDE_CONFIG_DIR" ? "/home/me/.claude" : null)), "claude-code");

        elsewhere.Due.Should().BeEmpty();
        elsewhere.Note.Should().Contain("CLAUDE_CONFIG_DIR");
        same.Due.Should().ContainSingle();
    }

    /// <summary>Correctness m2: a companion FILE whose size cannot be read is not "absent" — the unit is not whole.</summary>
    [Fact]
    public void A_companion_file_that_cannot_be_read_keeps_its_unit_as_not_whole()
    {
        File("/home/me/.gemini/antigravity-cli/conversations/c1.db");
        File("/home/me/.gemini/antigravity-cli/annotations/c1.pbtxt");
        var files = new UnreadableSize(_sandbox.Files, "c1.pbtxt");

        var antigravity = Of(Selection.Select(Input(files: files)), "antigravity");

        antigravity.Due.Should().BeEmpty();
        antigravity.Skipped.Should().ContainSingle().Which.SkipRule.Should().Be(SkipRule.NotWhole);
    }

    /// <summary>Correctness m3: a session whose main file is under its quarantine name no longer matches the glob — its companions'
    /// quarantined files are counted through the id of the quarantined name.</summary>
    [Fact]
    public void A_whole_session_under_its_quarantine_names_is_counted()
    {
        File("/home/me/.claude/projects/p/s1.jsonl.wsl-care-q-r1");
        File("/home/me/.claude/projects/p/s1/subagents/a.jsonl.wsl-care-q-r1");
        File("/home/me/.claude/file-history/s1/v1.wsl-care-q-r1");

        Of(Selection.Select(Input()), "claude-code").Quarantined.Should().Be(3);
    }

    private static string Manual(string name, string folder, string glob) =>
        $$"""{ "cli": "/home/me/.local/bin/{{name}}", "side": "wsl", "name": "{{name}}", "dataFolders": ["{{folder}}"], "sessionGlob": "{{glob}}" }""";

    /// <summary>Correctness m5: a glob that REACHES memory (a manual agent's <c>**</c>) still never selects it — the walk never
    /// enters it and the never-move rule refuses it; over random trees, manual agent included.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Memory_is_never_selected_even_by_a_glob_that_reaches_it(int seed)
    {
        var random = new Random(seed);
        string[] names = ["memory", "Memory", "MEMORY", "notes", "s1", "x"];
        for (var i = 0; i < 30; i++)
        {
            var depth = random.Next(0, 3);
            var folder = "/home/me/.mycli" + string.Concat(Enumerable.Range(0, depth).Select(_ => "/" + names[random.Next(names.Length)]));
            try
            {
                File($"{folder}/{names[random.Next(names.Length)]}.log");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A tree that cannot hold this name holds the others.
            }
        }

        File("/home/me/.mycli/memory/m.log");
        var config = Config($$"""{ "aiAgents": { "extra": [{{Manual("mycli", "/home/me/.mycli", "**")}}] }, "archive": { "agents": ["manual:mycli"] } }""");

        var mine = Of(Selection.Select(Input(config)), "manual:mycli");

        mine.Due.Should().NotBeEmpty("the tree holds files outside memory");
        mine.Due.Concat(mine.Skipped).SelectMany(u => u.Files).Should().NotContain(f => f.Relative.Split('/').Any(s => s.Equals("memory", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Correctness m8: an agent's own retention is known (a number of days) or unknown with why — never a null that could
    /// mean either; unknown is said.</summary>
    [Fact]
    public void An_agents_own_retention_is_known_or_unknown_with_why()
    {
        var selected = Selection.Select(Input());

        Of(selected, "claude-code").Retention.Should().BeOfType<RetentionFound.Known>().Which.Days.Should().Be(30);
        var antigravity = Of(selected, "antigravity").Retention.Should().BeOfType<RetentionFound.Unknown>().Subject;
        antigravity.Why.Should().Contain("documentation NOT read");
        antigravity.Warnings.Should().ContainSingle().Which.Should().Contain("not known");
    }

    /// <summary>Correctness m1: a companion folder is walked with the time the listing has LEFT — out of time, its session is not
    /// whole, never listed as if it were.</summary>
    [Fact]
    public void A_companion_walk_has_only_the_time_left()
    {
        File("/home/me/.claude/projects/p/s1.jsonl");
        File("/home/me/.claude/projects/p/s1/subagents/a.jsonl");

        var claude = Of(Selection.Select(Input() with { TimeLeft = static () => TimeSpan.FromTicks(1) }), "claude-code");

        claude.Due.Should().BeEmpty();
        claude.Skipped.Should().ContainSingle().Which.SkipRule.Should().Be(SkipRule.NotWhole);
    }

    /// <summary>A file system whose answer about one file's size is "cannot be read".</summary>
    private sealed class UnreadableSize(IFileSystem inner, string name) : DelegatingFileSystem(inner)
    {
        public override FileSizeResult FileSize(string path) =>
            path.EndsWith(name, StringComparison.Ordinal) ? new FileSizeResult.Unreadable("permission denied (a test)") : base.FileSize(path);
    }
}
