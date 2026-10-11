using System.Text;

using FluentAssertions;

namespace WslCare.Scenarios;

/// <summary>
/// No committed fixture or golden carries a person (E5 code round, 2026-10-04 — this repository is public): every file
/// under every <c>fixtures</c> / <c>golden</c> directory of the repository is scanned by <see cref="FixturePrivacy"/> on
/// every OS, against fixed rules AND the user name of the machine running the test. A planted instance of each rule shows
/// the scan bites, and a known file shows the walk still reaches the trees it is about — a scan that matches nothing
/// would otherwise pass forever.
/// </summary>
public sealed class FixturePrivacyTests
{
    private static readonly IReadOnlyList<string> NoMachineNames = [];

    [Fact]
    public void No_committed_fixture_or_golden_carries_a_home_or_profile_name_an_e_mail_address_or_this_machines_user_name()
    {
        var names = FixturePrivacy.MachineUserNames();
        var findings = FixturePrivacy.ScannedFiles(ReleaseFiles.Root)
            .SelectMany(file => FixturePrivacy.Findings(file, File.ReadAllText(Path.Combine(ReleaseFiles.Root, file), Encoding.UTF8), names))
            .ToList();

        findings.Should().BeEmpty(
            "a committed fixture or golden is published with this public repository: anonymise it through FixtureIdentity (FixtureAnonymisationTests, WSL_CARE_ANONYMISE_FIXTURES=1) and regenerate the goldens — {0} finding(s), the first 40:\n{1}",
            findings.Count,
            string.Join('\n', findings.Take(40)));
    }

    /// <summary>The same rules over EVERY tracked text file — the README, the plan, POST_DEPLOY, docs, workflows, the
    /// extension, research notes and sources alike — with only <see cref="FixturePrivacy.SyntheticNames"/> admitted
    /// besides <c>user</c>. A finding is counted per file and rule, never shown with its value.</summary>
    [Fact]
    public void No_tracked_text_file_carries_a_real_home_or_profile_name_an_e_mail_address_or_this_machines_user_name()
    {
        var files = FixturePrivacy.RepositoryTextFiles(ReleaseFiles.Root);
        var findings = FixturePrivacy.RepositoryFindings(ReleaseFiles.Root, FixturePrivacy.MachineNameCandidates());

        files.Should().Contain(f => f.Path == "README.md").And.Contain(f => f.Path == "todo/PLAN_wsl_care_daemon.md")
            .And.Contain(f => f.Path == ".github/workflows/release-extension.yml").And.Contain(f => f.Path == "src_vs_code/src/extension.ts")
            .And.Contain(f => f.Path == "contracts/golden/head/status.json", "the walk reaches every kind of tracked text file");
        findings.Should().BeEmpty("no tracked text file of this public repository may carry a person — {0} finding(s), by file and rule:\n{1}",
            findings.Count, ByFileAndRule(findings));
    }

    /// <summary>CI run 37202261532 (2026-10-04): the machine-name rule read the runner's own account — <c>runner</c> on the
    /// Linux images, <c>runneradmin</c> on Windows — and <c>runner</c> is an ordinary word of this repository. A machine
    /// running under a service account or a synthetic name, or under a name shorter than the floor, has no person to find.</summary>
    [Theory]
    [InlineData("runner")]
    [InlineData("runneradmin")]
    [InlineData("RUNNER")]
    [InlineData("vscode")]
    [InlineData("codespace")]
    [InlineData("root")]
    [InlineData("user")]
    [InlineData("alice")]
    [InlineData("me")]
    [InlineData("ab")]
    public void A_service_or_synthetic_account_as_the_machine_name_finds_nothing_in_the_repository(string machineName)
    {
        var findings = FixturePrivacy.RepositoryFindings(ReleaseFiles.Root, [machineName, machineName.ToUpperInvariant()]);

        findings.Should().BeEmpty("'{0}' is not a person — {1} finding(s), by file and rule:\n{2}", machineName, findings.Count, ByFileAndRule(findings));
    }

    /// <summary>The other direction: a distinctive machine name is still found — in a sentence, in any case, as a path
    /// segment — and only as a WHOLE word, never inside a longer one; a service account beside it changes nothing.</summary>
    [Fact]
    public void A_distinctive_machine_name_planted_in_a_tree_is_found_as_a_whole_word_or_path_segment_only()
    {
        using var tree = new WslCare.TestSupport.TempRoot("privacy-planted");
        tree.File("notes.txt", "built by Quillonvex today\nxquillonvexy and quillonvexes are other words\ncwd /home/user/QUILLONVEX/git\n");
        tree.File("runner.txt", "the test runner starts the command runner\n");

        var findings = FixturePrivacy.RepositoryFindings(tree.Path, ["runner", "Quillonvex", null, " "]);

        findings.Should().Equal(
            "notes.txt:1 holds the user name #1 of the machine running this test (not printed)",
            "notes.txt:3 holds the user name #1 of the machine running this test (not printed)");
    }

    /// <summary>The service accounts and the name floor are the .vsix check's knowledge too, and one decision: the
    /// <c>SERVICE_ACCOUNTS</c> list of <c>src_vs_code/src/test/support/vsixCheck.ts</c> is EQUAL to
    /// <see cref="FixturePrivacy.ServiceAccounts"/> — read out of the TypeScript source, never retyped (PR #47, run
    /// 37751902435: the .vsix check had kept <c>runner</c> as a person and failed on the CHANGELOG's prose) — and
    /// <c>machineUserNameSplit</c> drops what is shorter than <see cref="FixturePrivacy.MinimumNameLength"/> as this scan
    /// does.</summary>
    [Fact]
    public void The_service_accounts_and_the_name_floor_are_the_vsix_checks_own()
    {
        var vsixCheck = File.ReadAllText(Path.Combine(ReleaseFiles.Root, "src_vs_code", "src", "test", "support", "vsixCheck.ts"));

        VsixServiceAccounts(vsixCheck).Should().NotBeEmpty("vsixCheck.ts declares SERVICE_ACCOUNTS (the read still finds the list)")
            .And.BeEquivalentTo(FixturePrivacy.ServiceAccounts, "one list of service accounts for both machine-name checks");
        System.Text.RegularExpressions.Regex.Match(vsixCheck, @"c\.length >= (?<floor>\d+)").Groups["floor"].Value
            .Should().Be(FixturePrivacy.MinimumNameLength.ToString(System.Globalization.CultureInfo.InvariantCulture), "one floor for a machine name in both checks");
    }

    /// <summary>The quoted names of the ONE live <c>SERVICE_ACCOUNTS</c> array literal in <paramref name="source"/>,
    /// every <c>//</c> comment of the source dropped first (a reason may hold an apostrophe or a <c>]</c>, and a
    /// commented-out copy is not the list). Empty when the declaration is not found; an
    /// <see cref="InvalidDataException"/> for two live declarations, or when the body holds anything but those names,
    /// commas and whitespace — an entry this read cannot see would otherwise leave the parity green while the lists
    /// differ.</summary>
    private static List<string> VsixServiceAccounts(string source)
    {
        var code = string.Join('\n', source.Split('\n').Select(line => line.Split("//")[0]));
        var declarations = System.Text.RegularExpressions.Regex.Matches(code, @"export const SERVICE_ACCOUNTS[^=]*=\s*\[(?<body>[^\]]*)\]");
        if (declarations.Count > 1)
        {
            throw new InvalidDataException($"vsixCheck.ts holds {declarations.Count} declarations of SERVICE_ACCOUNTS — the parity cannot tell which is the list");
        }
        var body = declarations.Count == 0 ? string.Empty : declarations[0].Groups["body"].Value;
        const string QuotedName = @"'(?<name>[^']+)'";
        var rest = System.Text.RegularExpressions.Regex.Replace(body, QuotedName, string.Empty);
        if (!System.Text.RegularExpressions.Regex.IsMatch(rest, @"^[\s,]*$"))
        {
            throw new InvalidDataException($"SERVICE_ACCOUNTS holds something other than single-quoted names: '{rest.Trim()}'");
        }

        return [.. System.Text.RegularExpressions.Regex.Matches(body, QuotedName).Select(m => m.Groups["name"].Value)];
    }

    [Fact]
    public void The_service_account_read_finds_each_quoted_name_and_ignores_the_comments()
    {
        const string source = "export const SERVICE_ACCOUNTS: readonly string[] = [\n  'runner', // it's the image's\n  'root', // a 'quoted' word\n];\nconst other = ['x'];";

        VsixServiceAccounts(source).Should().Equal("runner", "root");
        VsixServiceAccounts("const nothing = ['runner'];").Should().BeEmpty();
    }

    /// <summary>An entry the read cannot see — double-quoted, a template literal, a spread, a block comment — would
    /// let the TypeScript list grow while the parity test stays green (own review of this change, 2026-10-08), so the
    /// read refuses any array body that is not single-quoted names, commas, whitespace and line comments.</summary>
    [Theory]
    [InlineData("'runner',\n  \"builder\",")]
    [InlineData("'runner',\n  `builder`,")]
    [InlineData("'runner',\n  ...EXTRA,")]
    [InlineData("'runner', /* 'x' */")]
    public void The_service_account_read_refuses_an_entry_it_cannot_see(string body)
    {
        var source = $"export const SERVICE_ACCOUNTS: readonly string[] = [\n  {body}\n];";

        FluentActions.Invoking(() => VsixServiceAccounts(source)).Should().Throw<InvalidDataException>();
    }

    /// <summary>Comments are dropped before the declaration is looked for (coai code rounds, 2026-10-08): a
    /// commented-out copy above the live one is never read in its place, a <c>]</c> inside a line comment never ends the
    /// list early, and two LIVE declarations are refused.</summary>
    [Fact]
    public void The_service_account_read_sees_only_the_live_declaration_whole()
    {
        const string commentedCopy = "// export const SERVICE_ACCOUNTS = ['runner'];\nexport const SERVICE_ACCOUNTS: readonly string[] = [\n  'runner',\n  'root',\n];";
        const string bracketInComment = "export const SERVICE_ACCOUNTS: readonly string[] = [\n  'runner', // the image's [Linux] account ]\n  'root',\n];";
        const string twoLive = "export const SERVICE_ACCOUNTS = ['runner'];\nexport const SERVICE_ACCOUNTS = ['root'];";

        VsixServiceAccounts(commentedCopy).Should().Equal("runner", "root");
        VsixServiceAccounts(bracketInComment).Should().Equal("runner", "root");
        FluentActions.Invoking(() => VsixServiceAccounts(twoLive)).Should().Throw<InvalidDataException>().WithMessage("*2 declarations*");
    }

    private static string ByFileAndRule(IEnumerable<string> findings) =>
        string.Join('\n', findings
            .GroupBy(f => System.Text.RegularExpressions.Regex.Replace(f, @":\d+ holds ", " holds "), StringComparer.Ordinal)
            .Select(g => $"{g.Count(),4} × {g.Key}")
            .Order(StringComparer.Ordinal));

    [Fact]
    public void The_repository_scan_admits_only_the_listed_synthetic_names_besides_user()
    {
        string[] none = [];
        var unlisted = new HashSet<string>(StringComparer.Ordinal) { "planted" };

        FixturePrivacy.Findings("f", "cwd /home/planted/x", none, unlisted).Should().BeEmpty("an allowlisted synthetic name is admitted");
        // Spelt in two parts so this source file does not itself hold a foreign home for the repository scan to find.
        const string stranger = "/home/" + "stranger/x and /mnt/c/Users/" + "stranger";
        FixturePrivacy.Findings("f", $"cwd {stranger}", none, unlisted).Should().HaveCount(2, "anything else is a finding");
        FixturePrivacy.Findings("f", "cwd /home/planted/x", none).Should().ContainSingle("the fixture scan stays strict: only user");
    }

    [Theory]
    [InlineData("Co-Authored-By: Claude <noreply@anthropic.com>")]
    [InlineData("GIT_AUTHOR_EMAIL=test@example.invalid; contact x.y@example.org; a@b.example.com")]
    [InlineData("user@example.invalid and root@host.test")]
    public void Service_and_reserved_example_addresses_are_not_findings(string line)
    {
        FixturePrivacy.Findings("f", line, NoMachineNames).Should().BeEmpty();
    }

    [Fact]
    public void The_walk_reaches_the_captured_trees_and_the_goldens_it_exists_for()
    {
        var files = FixturePrivacy.ScannedFiles(ReleaseFiles.Root);

        files.Should().Contain([
            "contracts/golden/head/status.json",
            "src_daemon/tests/fixtures/procfs/ubuntu-2026-10-02/etc/passwd",
            "src_daemon/tests/fixtures/procfs/ubuntu-2026-10-02/links.txt",
            "src_daemon/tests/fixtures/procfs/ubuntu-2026-10-02/proc/5814/cmdline",
            "src_daemon/tests/fixtures/docker/ubuntu-2026-10-02/ps-a.out",
            "src_daemon/tests/fixtures/health/ubuntu-2026-10-02/powershell-clock.out",
            "src_daemon/tests/WslCare.Scenarios/fixtures/synthetic/harness-self-test.txt",
        ]);
        files.Should().NotContain(f => f.Contains("/bin/", StringComparison.Ordinal) || f.Contains("/obj/", StringComparison.Ordinal) || f.StartsWith(".agents/", StringComparison.Ordinal),
            "build copies and the shared-rules submodule are not this repository's fixtures");
    }

    [Theory]
    [InlineData("cwd /home/alice/git/x", "a /home/<name> path")]
    [InlineData("\"value\": \"/golden-root/home/me/.npm does not exist\"", "a /home/<name> path")]
    [InlineData("/mnt/c/Users/alice/AppData/Local/Temp", "a Windows profile path")]
    [InlineData("C:\\Users\\alice", "a Windows profile path")]
    [InlineData("\"profile\": \"C:\\\\Users\\\\alice\\\\.wslconfig\"", "a Windows profile path")]
    // A non-reserved domain, spelt in two parts so this file holds no address for the repository scan to find.
    [InlineData("contact a.person@" + "mailbox.local", "an e-mail address")]
    public void Each_rule_catches_its_planted_instance(string line, string rule)
    {
        FixturePrivacy.Findings("planted.txt", $"first line\n{line}\n", NoMachineNames)
            .Should().ContainSingle().Which.Should().StartWith("planted.txt:2 holds ").And.Contain(rule);
    }

    /// <summary>2026-10-11: a copied A21 basis carried the owner's login name into POST_DEPLOY.md and a research note, and the scan
    /// saw it ONLY through the machine-name rule — on the owner's machine, never in CI (the runner's account is <c>runner</c>). The
    /// daemon's own sentences that name a target user are a FIXED rule now, so the repository is clean whatever the machine running
    /// the test is called.</summary>
    [Fact]
    public void No_tracked_text_file_carries_a_daemon_sentence_naming_a_real_user_whatever_the_machine_is_called()
    {
        var findings = FixturePrivacy.RepositoryFindings(ReleaseFiles.Root, []);

        findings.Should().BeEmpty("the fixed rules alone must hold the repository clean, as CI runs them — {0} finding(s), by file and rule:\n{1}",
            findings.Count, ByFileAndRule(findings));
    }

    /// <summary>The holes of a daemon interpolation that mention a user but carry no NAME — each with why. Anything else whose
    /// expression mentions a user, an account or a login is a name, and its sentence must be one the rule knows.</summary>
    private static readonly HashSet<string> NotANameHole = new(StringComparer.Ordinal)
    {
        "c.Loaded.UserLayerSkipped",  // a count of skipped configuration layers
        "context.TargetUser.Refusal", // the daemon's own refusal sentence, its names already covered where it is written
        "account.Home",               // a home path: the /home/<name> rule's
        "logins.Count",               // a number
        "FirstLoginUid",              // a number
        "AccountTag",                 // a constant tag of the Windows disk script
        "user.Source",                // where the target user came from (SUDO_USER, the login uid …)
    };

    /// <summary>ActCommand's helper renders "{name} ({source})", which its caller writes after "target user: " — the caller's hole
    /// (<c>User(p.TargetUser)</c>) is the sentence, so the helper's own line is not one.</summary>
    private const string CompositionHelper = "private static string User(TargetUserReport user)";

    private const string PlantedName = "quillonvex";

    /// <summary>Coai code round of this change: a NEW daemon sentence that interpolates a user name would slip past the fixed rule
    /// while the repository scan stayed green. So every such interpolation of <c>src_daemon/src</c> is found here, rendered with a
    /// planted name, and must be caught by the rule — a new site fails this test until the rule (or <see cref="NotANameHole"/>, with
    /// its reason) knows it.</summary>
    [Fact]
    public void Every_daemon_sentence_that_interpolates_a_user_name_is_covered_by_the_rule()
    {
        var src = Path.Combine(ReleaseFiles.Root, "src_daemon", "src");
        var sites = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(src, file).Replace('\\', '/'))
            .Where(file => !file.Contains("/obj/", StringComparison.Ordinal) && !file.Contains("/bin/", StringComparison.Ordinal))
            .SelectMany(file => File.ReadLines(Path.Combine(src, file)).Select((text, i) => (File: file, Line: i + 1, Text: text)))
            .Where(line => !line.Text.Contains(CompositionHelper, StringComparison.Ordinal))
            .SelectMany(line => System.Text.RegularExpressions.Regex.Matches(line.Text, @"\{(?<expr>[A-Za-z_][A-Za-z0-9_.()]*)(?::[^{}]*)?\}")
                .Select(m => (line.File, line.Line, line.Text, Hole: m.Value, Expr: m.Groups["expr"].Value)))
            .Where(h => System.Text.RegularExpressions.Regex.IsMatch(h.Expr, "user|account|login", System.Text.RegularExpressions.RegexOptions.IgnoreCase) && !NotANameHole.Contains(h.Expr))
            .ToList();

        sites.Should().HaveCountGreaterThanOrEqualTo(12, "the walk finds the interpolations of a user name it exists for");
        var uncovered = sites
            .Where(h => !FixturePrivacy.Findings("site", Rendered(h.Text, h.Hole), NoMachineNames, FixturePrivacy.SyntheticNames).Any(f => f.Contains("daemon sentence", StringComparison.Ordinal)))
            .Select(h => $"{h.File}:{h.Line} {h.Hole}")
            .ToList();
        uncovered.Should().BeEmpty("every daemon sentence that writes a user name must be one the privacy rule finds — teach FixturePrivacy.DaemonUserSentence its shape, or say in NotANameHole why the hole is no name:\n{0}", string.Join('\n', uncovered));
    }

    /// <summary>The source line with the name hole filled by the planted name and every other hole by <c>1</c>.</summary>
    private static string Rendered(string line, string hole) =>
        System.Text.RegularExpressions.Regex.Replace(line.Replace(hole, PlantedName, StringComparison.Ordinal), @"\{(?!\s)[^{}]*\}", "1");

    // Each planted name is spelt in two parts, so this source file does not itself hold a daemon sentence for the repository scan to find.
    [Theory]
    [InlineData("its basis read \"0 process(es), 0 fd link(s) in 0 ms … 0 relay(s) of " + "quillonvex kept\"")]
    [InlineData("2 AI-agent process(es) of " + "quillonvex kept: 12 (busy)")]
    [InlineData("1 MCP server(s) of " + "quillonvex kept: 40 (busy)")]
    [InlineData("wsl-care act --preview (nothing run, nothing written); target user: " + "quillonvex (SUDO_USER)")]
    [InlineData("config set and config reset write the user layer of " + "quillonvex; run them as " + "quillonvex, not as root")]
    [InlineData("npm is not in " + "quillonvex's bin folders: /usr/local/bin")]
    public void A_daemon_sentence_naming_a_real_user_is_a_finding(string line)
    {
        FixturePrivacy.Findings("planted.txt", $"first line\n{line}\n", NoMachineNames)
            .Should().ContainSingle().Which.Should().Be("planted.txt:2 holds a daemon sentence that names its target user, whose name is not 'user'");
    }

    [Theory]
    [InlineData("0 relay(s) of user kept: none")]
    [InlineData("1 relay(s) of me kept: 12 (its client is alive)")]
    [InlineData("2 relay(s) of <user> kept")]
    [InlineData("target user: user (SUDO_USER)")]
    [InlineData("write the user layer of <user>; run them as user, not as root")]
    [InlineData("npm is not in user's bin folders")]
    [InlineData("the relay(s) of the run were kept as they were")]
    [InlineData("Nothing happens until the CodeRabbit App is installed for this repository (docs/repo-settings.md, step 7).")]
    [InlineData("dotnet is not installed for the target user: no dotnet")]
    [InlineData("restore archived AI-agent sessions as user: 1 removed at their source and verified")]
    public void A_daemon_sentence_with_the_placeholder_or_a_synthetic_name_is_not_a_finding(string line)
    {
        FixturePrivacy.Findings("clean.txt", line, NoMachineNames, FixturePrivacy.SyntheticNames).Should().BeEmpty();
    }

    [Theory]
    [InlineData("/home/user/git/project-a")]
    [InlineData("/mnt/c/Users/user/AppData/Local/Programs/Microsoft VS Code")]
    [InlineData("C:\\Users\\Public\\Desktop")]
    [InlineData("[{\"unit\":\"getty@tty1.service\",\"load\":\"loaded\"}] and user@1000.service")]
    [InlineData("  - /home/<user> -> /home/user;")]
    [InlineData("npm exec @playwright/mcp@latest")]
    public void Anonymised_values_systemd_template_units_and_placeholders_are_not_findings(string line)
    {
        FixturePrivacy.Findings("clean.txt", line, NoMachineNames).Should().BeEmpty();
    }

    [Fact]
    public void The_machine_user_name_is_caught_as_a_whole_word_in_any_case_and_never_printed()
    {
        string[] names = ["Planted-Name"];

        var findings = FixturePrivacy.Findings("f.txt", "a\nsudo -u planted-name docker ps\nunplanted-namex\n", names);

        findings.Should().Equal("f.txt:2 holds the user name #1 of the machine running this test (not printed)");
        string.Join('\n', findings).Should().NotContain("lanted", "the value itself is what must not be repeated");
    }

    [Fact]
    public void The_machine_names_leave_out_blanks_duplicates_short_names_service_accounts_and_synthetic_names()
    {
        FixturePrivacy.Personal(["Quillonvex", "QUILLONVEX", " ", null, "ab", "user", "root", "runner", "RunnerAdmin", "vscode", "codespace", "alice", "Bob", "Marrowind"])
            .Should().Equal("Quillonvex", "Marrowind");
    }
}
