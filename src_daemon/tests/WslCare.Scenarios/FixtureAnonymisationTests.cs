using System.Text;

using FluentAssertions;

using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// The captured fixtures are anonymised through the ONE identity list (<see cref="FixtureIdentity"/>, E5 code round,
/// 2026-10-04): applying it to every text file of <c>src_daemon/tests/fixtures/</c> changes nothing. Re-anonymise a new
/// capture with <c>WSL_CARE_ANONYMISE_FIXTURES=1 ./WslCare.Scenarios --filter-class "*FixtureAnonymisationTests"</c>
/// (any OS — it is text), then regenerate the goldens and review both diffs. The list's rules, each on a planted corpus,
/// are <c>FixtureIdentityTests</c>.
/// </summary>
public sealed class FixtureAnonymisationTests
{
    /// <summary>Set to <c>1</c> to REWRITE the fixtures in place instead of only checking them.</summary>
    public const string WriteVariable = "WSL_CARE_ANONYMISE_FIXTURES";

    private static string FixturesRoot => Path.Combine(ReleaseFiles.Root, "src_daemon", "tests", "fixtures");

    private static readonly UTF8Encoding NoBom = new(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public void Every_captured_fixture_is_already_what_the_identity_list_makes_of_it()
    {
        var files = FixtureIdentity.TextFiles(FixturesRoot);
        var identity = FixtureIdentity.Learn(files.Select(f => f.Text));
        var changed = files.Where(f => identity.Apply(f.Text) != f.Text).ToList();

        if (Environment.GetEnvironmentVariable(WriteVariable) == "1")
        {
            foreach (var (path, text) in changed)
            {
                File.WriteAllBytes(Path.Combine(FixturesRoot, path), NoBom.GetBytes(identity.Apply(text)));
            }

            return;
        }

        changed.Select(f => f.Path).Should().BeEmpty($"every captured fixture passes through the identity list unchanged — rewrite with {WriteVariable}=1, regenerate the goldens, review the diff");
    }

    /// <summary>The companion: the anonymised trees still carry the shapes the list rewrites, so "nothing to change" means
    /// "already neutral", not "the list no longer matches anything".</summary>
    [Fact]
    public void The_anonymised_trees_still_carry_every_neutral_shape_the_list_produces()
    {
        var corpus = string.Join('\n', FixtureIdentity.TextFiles(FixturesRoot).Select(f => f.Text));
        string[] shapes =
        [
            "user:x:1000:1000::/home/user:/bin/bash",
            "/home/user/git/project-a",
            "/mnt/c/Users/user/",
            @"C:\Users\user",
            "/.vscode-server/extensions/vendor.extension-",
            $"\t{FixtureIdentity.Temp}\n",
        ];

        shapes.Where(shape => !corpus.Contains(shape, StringComparison.Ordinal)).Should().BeEmpty(
            "the anonymised captures carry the login account, a project folder, a profile path in both forms, an extension folder and the scratchpad cwd");
    }
}

/// <summary>Each rule of <see cref="FixtureIdentity"/> on a planted corpus — the shapes it must rewrite, the values it
/// must keep, consistency across files and idempotence.</summary>
public sealed class FixtureIdentityTests
{
    private static readonly string[] Corpus =
    [
        "root:x:0:0::/root:/bin/bash\nsystemd-resolve:x:991:991::/:/usr/sbin/nologin\nalice:x:1000:1000::/home/alice:/bin/bash\n",
        "proc/1/cwd\t/home/alice/git/zeta\nproc/2/cwd\t/home/alice/git/alpha\nproc/4/cwd\t/home/alice/git/project-tools\nproc/3/cwd\t/mnt/c/Users/Bob/AppData/Local/Temp/claude/d--x/0f0f/scratchpad\n",
        "node\0/home/alice/.vscode-server/extensions/pub.tool-1.2.3-linux-x64/dist/x.js\0--add-dir=/home/alice/git/alpha\0alice\0"
            + "/mnt/c/Users/Bob/AppData/Roaming/Code/User/globalStorage/me.other/bin/c.exe\0sh\0/mnt/c/Users/Bob/AppData/Local/Temp/claude/d--x/0f0f/scratchpad/capture.sh\0"
            + "--log-directory\0/home/alice/.vscode-server/data/logs/x/exthost1/pub.tool\0",
        "2026-10-02T17:34:47Z\nC:\\Users\\Bob\nmail a.person@example.org; getty@tty1.service failed; user@1000.service\n",
    ];

    private static string[] Anonymised()
    {
        var identity = FixtureIdentity.Learn(Corpus);
        return [.. Corpus.Select(identity.Apply)];
    }

    [Fact]
    public void A_login_account_becomes_user_in_passwd_in_every_home_and_as_an_argv_element_while_system_accounts_and_every_number_stay()
    {
        var result = Anonymised();

        result[0].Should().Be("root:x:0:0::/root:/bin/bash\nsystemd-resolve:x:991:991::/:/usr/sbin/nologin\nuser:x:1000:1000::/home/user:/bin/bash\n");
        result[2].Split('\0').Should().Contain("user", "the bare account name as an argv element").And.NotContain("alice");
        string.Concat(result).Should().NotContain("alice").And.NotContain("Bob");
    }

    [Fact]
    public void A_windows_profile_becomes_user_in_the_distro_form_and_the_drive_form()
    {
        var result = Anonymised();

        result[2].Should().Contain("/mnt/c/Users/user/AppData/Roaming/Code/User/globalStorage/");
        result[3].Should().Contain("C:\\Users\\user\n");
    }

    [Fact]
    public void Project_folders_get_neutral_names_in_ordinal_order_and_the_same_name_in_every_file()
    {
        var result = Anonymised();

        result[1].Should().Contain("proc/1/cwd\t/home/user/git/project-c\nproc/2/cwd\t/home/user/git/project-a\nproc/4/cwd\t/home/user/git/project-b\n", "alpha < project-tools < zeta");
        result[2].Should().Contain("--add-dir=/home/user/git/project-a", "the same project as links.txt's pid 2 — the cross-file join holds");
    }

    [Fact]
    public void A_real_project_called_project_word_is_a_name_like_any_other_never_mistaken_for_an_anonymised_one()
    {
        string.Concat(Anonymised()).Should().NotContain("project-tools", "only project-<one or two letters> is already neutral");
    }

    [Fact]
    public void Extension_folders_get_neutral_ids_their_version_neutral_and_their_platform_kept()
    {
        var result = Anonymised();

        result[2].Should().Contain("/.vscode-server/extensions/vendor.extension-b-1.0.0-linux-x64/dist/x.js", "pub.tool sorts after me.other")
            .And.Contain("/globalStorage/vendor.extension-a/bin/c.exe")
            .And.Contain("/exthost1/vendor.extension-b\0", "the same id where it recurs outside an extensions folder — an extension host's log folder")
            .And.NotContain("pub.tool").And.NotContain("me.other");
    }

    [Fact]
    public void A_temporary_folder_becomes_tmp_x_keeping_a_final_file_name()
    {
        var result = Anonymised();

        result[1].Should().EndWith($"proc/3/cwd\t{FixtureIdentity.Temp}\n");
        result[2].Should().Contain($"\0sh\0{FixtureIdentity.Temp}/capture.sh\0");
    }

    [Fact]
    public void An_e_mail_address_is_replaced_and_a_systemd_template_instance_is_not()
    {
        Anonymised()[3].Should().Contain("mail user@example.invalid; getty@tty1.service failed; user@1000.service");
    }

    [Fact]
    public void The_list_is_idempotent_and_never_hands_out_a_neutral_name_already_in_use()
    {
        var once = Anonymised();
        var again = FixtureIdentity.Learn(once);
        once.Select(again.Apply).Should().Equal(once, "a second pass over anonymised text changes nothing");

        var mixed = FixtureIdentity.Learn(["/home/user/git/project-a\n/home/user/git/newer\n"]);
        mixed.Apply("/home/user/git/newer").Should().Be("/home/user/git/project-b", "project-a is taken");
    }

    [Fact]
    public void Letter_names_run_a_to_z_then_aa()
    {
        new[] { 0, 1, 25, 26, 27, 51, 52 }.Select(FixtureIdentity.LetterName).Should().Equal("a", "b", "z", "aa", "ab", "az", "ba");
    }

    [Fact]
    public void Every_rule_is_named_and_says_what_it_finds_and_what_it_puts_in_its_place()
    {
        FixtureIdentity.Rules.Select(r => r.Name).Should().OnlyHaveUniqueItems().And.HaveCount(8);
        FixtureIdentity.Rules.Should().OnlyContain(r => r.Finds.Length > 0 && r.Becomes.Length > 0);
    }
}
