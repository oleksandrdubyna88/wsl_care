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

    /// <summary>
    /// The repository-wide scan's ONE allowlist: names the tests and docs INVENT for a /home or profile path — synthetic
    /// accounts, never a person's. Each entry says where it is used; a name not here, other than <c>user</c>, is a finding.
    /// The running machine's user names are checked regardless of this list.
    /// </summary>
    internal static readonly IReadOnlySet<string> SyntheticNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "me",      // the sandbox's own home (LinuxEnvironment.Sandboxed: <root>/home/me) and the target user of the action,
                   // config, path-rule and scenario tests
        "ann",     // a second invented login account (ActionEngineTests, UserCacheTests)
        "sam",     // a third (TargetHomeTests)
        "alice",   // invented accounts of the installer, path, privacy and identity tests (InstallWorld, HostPathsTests,
                   // HealthTests, FixturePrivacyTests, FixtureIdentityTests, GoldenContractTests); case-insensitive, as
                   // every entry: PathRulesTests folds "me" to "ME" on purpose
        "Bob",     // the invented Windows profile of FixtureIdentityTests
        "zed",     // the invented --set-default-user of InstallWorld
        "u",       // the one-letter home of GoldenContractTests' normaliser input
        "x",       // a placeholder home in the scanners' own planted instances (vsixCheck.test.ts, FixturePrivacy.cs)
        "planted", // the planted name of this class's allowlist test
    };

    /// <summary>The same rules over EVERY tracked text file — the README, the plan, POST_DEPLOY, docs, workflows, the
    /// extension, research notes and sources alike — with only <see cref="SyntheticNames"/> admitted besides <c>user</c>.
    /// A finding is counted per file and rule, never shown with its value.</summary>
    [Fact]
    public void No_tracked_text_file_carries_a_real_home_or_profile_name_an_e_mail_address_or_this_machines_user_name()
    {
        var names = FixturePrivacy.MachineUserNames();
        var files = FixturePrivacy.RepositoryTextFiles(ReleaseFiles.Root);
        var findings = files.SelectMany(f => FixturePrivacy.Findings(f.Path, f.Text, names, SyntheticNames)).ToList();
        var byFile = findings
            .GroupBy(f => System.Text.RegularExpressions.Regex.Replace(f, @":\d+ holds ", " holds "), StringComparer.Ordinal)
            .Select(g => $"{g.Count(),4} × {g.Key}")
            .Order(StringComparer.Ordinal);

        files.Should().Contain(f => f.Path == "README.md").And.Contain(f => f.Path == "todo/PLAN_wsl_care_daemon.md")
            .And.Contain(f => f.Path == ".github/workflows/release-extension.yml").And.Contain(f => f.Path == "src_vs_code/src/extension.ts")
            .And.Contain(f => f.Path == "contracts/golden/head/status.json", "the walk reaches every kind of tracked text file");
        findings.Should().BeEmpty("no tracked text file of this public repository may carry a person — {0} finding(s), by file and rule:\n{1}",
            findings.Count, string.Join('\n', byFile));
    }

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
    public void The_machine_names_leave_out_blanks_duplicates_and_the_names_the_anonymised_data_or_the_system_use()
    {
        FixturePrivacy.Personal(["alice", "ALICE", " ", null, "user", "root", "bob"]).Should().Equal("alice", "bob");
    }
}
