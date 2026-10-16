using System.Diagnostics;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Processes.Redaction;

using static WslCare.Core.Tests.Processes.Redaction.RedactionCases;

namespace WslCare.Core.Tests.Processes.Redaction;

/// <summary>E15.S7 (todo/PLAN_disk_health.md, "E15.S7 design" §6): ONE rule judges every command line the daemon shows — a secret
/// in a positional argument, in a NAME=value, in a structured blob, in a URL, behind an innocent flag, as a bare high-entropy
/// token — and the replacement is always the single constant, never a prefix, a length or a part of the value.</summary>
public sealed class CommandLineRedactionTests
{
    // 1 -------------------------------------------------------------------------------------------------------------
    [Fact]
    public void A_creds_config_key_given_as_a_positional_argument_is_replaced_and_the_program_and_subcommand_stay()
    {
        Shown("creds", "config", ConfigKey).Should().Be("creds config <redacted>");
    }

    [Fact]
    public void The_config_key_is_caught_by_its_shape_alone_even_when_the_entropy_floor_is_out_of_reach()
    {
        var outOfReach = Limits with { SecretMinChars = 64 };

        CommandLineRedaction.Shown(["creds", "config", ConfigKey], outOfReach).Should().Be("creds config <redacted>");
    }

    // 2 -------------------------------------------------------------------------------------------------------------
    [Fact]
    public void A_name_equals_value_with_a_secret_word_is_redacted_without_a_leading_dash()
    {
        Shown("env", "GITHUB_TOKEN=" + GitHubToken, "gh", "pr", "list").Should().Be("env GITHUB_TOKEN=<redacted> gh pr list");
        Shown("env", "ANTHROPIC_API_KEY=" + AnthropicKey, "claude").Should().Be("env ANTHROPIC_API_KEY=<redacted> claude");
    }

    // 3 -------------------------------------------------------------------------------------------------------------
    [Fact]
    public void A_secret_environment_variable_given_to_docker_loses_its_value_and_a_bare_name_stays()
    {
        Shown("docker", "run", "-e", "AWS_SECRET_ACCESS_KEY=wJalrXUtnInventedK7MDENGbPxRfiCYinvented", "-e", "PGPASSWORD", "image")
            .Should().Be("docker run -e AWS_SECRET_ACCESS_KEY=<redacted> -e PGPASSWORD image");
    }

    // 4 -------------------------------------------------------------------------------------------------------------
    [Fact]
    public void A_structured_positional_holding_a_secret_word_and_a_separator_is_redacted_whole_not_taken_for_a_path()
    {
        Shown("/usr/share/code/code", "/TelemetrySession:{\"CollectorApiKey\":\"invented-collector-value\"}")
            .Should().Be("/usr/share/code/code <redacted>");
    }

    // 5 -------------------------------------------------------------------------------------------------------------
    [Fact]
    public void A_user_and_password_given_to_curl_u_is_redacted_while_systemd_user_is_untouched()
    {
        Shown("curl", "-u", "alice:Pa55w0rdInvented", "https://api.example.invalid/").Should().Be("curl -u <redacted> https://api.example.invalid/");
        Shown("systemd", "--user", "--deserialize", "48").Should().Be("systemd --user --deserialize 48");
    }

    // 6 -------------------------------------------------------------------------------------------------------------
    [Fact]
    public void An_authorization_header_given_after_an_innocent_flag_is_redacted()
    {
        Shown("curl", "-H", "Authorization: Bearer " + Jwt).Should().Be("curl -H <redacted>");
        Shown("gh", "api", "-H", "Authorization: token " + GitHubToken).Should().Be("gh api -H <redacted>");
        Shown("curl", "-H", "Authorization: Basic dXNlcjpwYXNz").Should().Be("curl -H <redacted>");
    }

    // 7 -------------------------------------------------------------------------------------------------------------
    [Fact]
    public void A_credential_inside_a_url_loses_its_userinfo_and_the_host_and_path_stay()
    {
        Shown("git", "clone", "https://alice:" + GitHubToken + "@git.example.invalid/o/r").Should().Be("git clone https://<redacted>@git.example.invalid/o/r");
        Shown("git", "clone", "https://alice:Inv3nted@git.example.invalid/o/r").Should().Be("git clone https://<redacted>@git.example.invalid/o/r");
    }

    [Fact]
    public void A_url_assignments_value_that_fails_the_url_arm_is_replaced_whole_under_its_name()
    {
        Shown("env", "DATABASE_URL=postgres://app:Inv3nted@db.example.invalid/x", "psql").Should().Be("env DATABASE_URL=<redacted> psql");
    }

    [Fact]
    public void A_secret_query_parameter_of_a_url_is_redacted_and_the_rest_of_the_url_is_shown()
    {
        Shown("curl", "https://files.example.invalid/a.tgz?X-Amz-Signature=inventedSignature0123&dl=1")
            .Should().Be("curl https://files.example.invalid/a.tgz?X-Amz-Signature=<redacted>&dl=1");
    }

    [Fact]
    public void A_creds_shape_in_the_path_of_a_url_makes_the_whole_url_redacted()
    {
        Shown("curl", "https://files.example.invalid/" + GitHubToken + "/a.tgz").Should().Be("curl <redacted>");
    }

    // The URL arm runs BEFORE the creds shape (design §2.1 as amended 2026-10-16) -----------------------------------------
    [Fact]
    public void A_url_with_a_creds_shaped_userinfo_loses_only_the_userinfo_because_the_url_arm_runs_before_the_creds_shape()
    {
        Shown("git", "clone", "https://alice:" + Jwt + "@git.example.invalid/o/r").Should().Be("git clone https://<redacted>@git.example.invalid/o/r");
    }

    [Fact]
    public void A_creds_shaped_value_of_a_secret_query_parameter_is_cut_out_alone_and_the_url_stays()
    {
        Shown("curl", "https://api.example.invalid/x?token=" + GitHubToken + "&page=2").Should().Be("curl https://api.example.invalid/x?token=<redacted>&page=2");
    }

    [Fact]
    public void A_creds_shape_left_in_the_rest_of_a_url_after_its_userinfo_went_makes_the_whole_url_redacted()
    {
        Shown("curl", "https://alice:Inv3nted@api.example.invalid/" + GitHubToken + "/x").Should().Be("curl <redacted>");
    }

    // 5 (the bounded `key` word, design §2.2) ----------------------------------------------------------------------------
    [Fact]
    public void The_word_key_is_a_secret_word_only_as_a_whole_word_of_a_flag_or_a_name()
    {
        Shown("tool", "--key=abc12").Should().Be("tool --key=<redacted>");
        Shown("env", "API_KEY=abc12", "tool").Should().Be("env API_KEY=<redacted> tool");
        Shown("tool", "-key", "abc12").Should().Be("tool -key <redacted>");
    }

    [Fact]
    public void A_flag_that_merely_contains_the_letters_key_is_not_a_secret_flag()
    {
        Shown("setxkbmap", "--keyboard", "us").Should().Be("setxkbmap --keyboard us");
        Shown("tool", "--keep", "3").Should().Be("tool --keep 3");
    }

    // 6 (one invented case per creds family, design §2.3) -----------------------------------------------------------------
    [Theory]
    [MemberData(nameof(CredsFamilies), MemberType = typeof(RedactionCases))]
    public void Every_creds_family_is_caught_by_its_shape_alone_with_the_entropy_floor_out_of_reach(string family, string token)
    {
        var outOfReach = Limits with { SecretMinChars = 100_000 };

        CommandLineRedaction.Shown(["tool", token], outOfReach).Should().Be("tool <redacted>", $"the {family} shape");
    }

    // 8 ------------------------------------------------------------------------------------------------------------
    [Fact]
    public void A_password_flag_loses_its_value_in_both_flag_forms()
    {
        Shown("tool", "--password=Inv3ntedPass", "--port=80").Should().Be("tool --password=<redacted> --port=80");
        Shown("tool", "--password", "Inv3ntedPass", "--port", "80").Should().Be("tool --password <redacted> --port 80");
    }

    [Fact]
    public void A_program_specific_password_flag_is_redacted_only_for_its_program_and_a_docker_port_map_is_not_one()
    {
        Shown("mysql", "-pInv3ntedPass", "-h", "db.example.invalid").Should().Be("mysql -p<redacted> -h db.example.invalid");
        Shown("sshpass", "-p", "Inv3ntedPass", "ssh", "host.example.invalid").Should().Be("sshpass -p <redacted> ssh host.example.invalid");
        Shown("docker", "run", "-p", "8080:80", "image").Should().Be("docker run -p 8080:80 image");
    }

    // 9 -------------------------------------------------------------------------------------------------------------
    [Fact]
    public void A_script_holding_a_secret_word_before_a_separator_is_redacted_and_one_holding_none_is_shown()
    {
        Shown("node", "-e", "const token='inventedvalue'; console.log(token)").Should().Be("node -e <redacted>");
        Shown("node", "-e", "const net = require('net'); process.stdin.pause(); const client = net.createConnection({ host: '127.0.0.1', port: 41304 })")
            .Should().Be("node -e const net = require('net'); process.stdin.pause(); const client = net.createConnection({ host: '127.0.0.1', port: 41304 })");
    }

    // 10 ------------------------------------------------------------------------------------------------------------
    [Fact]
    public void A_connection_string_with_a_password_is_redacted_whole()
    {
        Shown("dotnet", "exec", "x.dll", "--connectionString", "Server=db;Password=Inv3nted").Should().Be("dotnet exec x.dll --connectionString <redacted>");
    }

    // 11 (the redaction half) ---------------------------------------------------------------------------------------
    [Fact]
    public void A_session_guid_is_shown_after_resume_in_both_forms()
    {
        Shown("claude", "--resume=" + SessionGuid).Should().Be("claude --resume=" + SessionGuid);
        Shown("claude", "-r", SessionGuid).Should().Be("claude -r " + SessionGuid);
    }

    // 12 ------------------------------------------------------------------------------------------------------------
    [Fact]
    public void A_bare_48_character_base64url_positional_is_redacted_by_its_entropy()
    {
        Shown("tool", Blob48).Should().Be("tool <redacted>");
    }

    [Fact]
    public void The_entropy_floor_is_the_key_not_a_constant()
    {
        var token = Blob48[..19];

        CommandLineRedaction.Shown(["tool", token], Limits).Should().Be("tool " + token, "19 characters are under the floor of 20");
        CommandLineRedaction.Shown(["tool", token], Limits with { SecretMinChars = 19 }).Should().Be("tool <redacted>", "the floor read from the key is 19");
    }

    // 13 ------------------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData("/home/user/.vscode-server/bin/9c1e7a3f5b2d8406a1f93c7e5b2d84a06f1e9c3b/node")]
    [InlineData("--add-dir=/home/user/git/project-tokenizer")]
    [InlineData("@playwright/mcp@latest")]
    [InlineData("ubuntu-24.04-20260101")]
    [InlineData("sourceGeneratorExecutionPreference")]
    [InlineData("rovodev-ide-vscode")]
    [InlineData("--sourceGeneratorExecutionPreference")]
    [InlineData("--devKitDependencyPath")]
    public void Paths_npm_specs_versions_flags_and_plain_identifiers_are_shown_as_they_are(string argument)
    {
        Shown("tool", argument).Should().Be("tool " + argument);
    }

    // 14 ------------------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData(Hex32)]
    [InlineData(Hex40)]
    [InlineData(Hex64)]
    public void A_bare_hex_positional_of_any_digest_length_is_redacted_there_is_no_digest_exemption(string hex)
    {
        Shown("tool", hex).Should().Be("tool <redacted>");
    }

    // 15 ------------------------------------------------------------------------------------------------------------
    public static TheoryData<string[], string> RedactedValues => new()
    {
        { ["creds", "config", ConfigKey], ConfigKey },
        { ["env", "GITHUB_TOKEN=" + GitHubToken, "x"], GitHubToken },
        { ["tool", "--password", "Inv3ntedHunter9"], "Inv3ntedHunter9" },
        { ["tool", "--password=Inv3ntedHunter9"], "Inv3ntedHunter9" },
        { ["tool", Blob48], Blob48 },
        { ["tool", Hex40], Hex40 },
        { ["curl", "-u", "alice:Pa55w0rdInvented"], "Pa55w0rdInvented" },
        { ["git", "clone", "https://alice:Inv3nted77@git.example.invalid/o/r"], "Inv3nted77" },
    };

    [Theory]
    [MemberData(nameof(RedactedValues))]
    public void A_redacted_value_leaves_no_prefix_no_suffix_and_no_length_behind(string[] argv, string value)
    {
        var shown = Shown(argv);

        shown.Should().Contain(Redacted);
        for (var at = 0; at + 4 <= value.Length; at++)
        {
            shown.Should().NotContain(value.Substring(at, 4), $"no 4-character piece of the value may reach the output (at {at})");
        }

        shown.Should().NotContain(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), "the replacement never tells the value's length");
    }

    // 16 ------------------------------------------------------------------------------------------------------------
    [Fact]
    public void A_cut_after_a_long_secret_ends_in_the_constant_or_a_prefix_of_it_never_in_the_secret()
    {
        var secret = string.Concat(Enumerable.Repeat(Blob48, 7))[..300];
        var filler = "/" + new string('a', 190);

        var shown = CommandLineRedaction.Shown(["node", filler, secret], Limits);

        shown.Should().HaveLength(Limits.ShownChars);
        Redacted.Should().StartWith(shown[(shown.LastIndexOf(' ') + 1)..], "the cut can only shorten the constant");
        shown.Should().NotContain(Blob48[..4]);
    }

    [Fact]
    public void A_cut_of_five_characters_shows_a_prefix_of_the_constant()
    {
        CommandLineRedaction.Shown([Blob48], Limits with { ShownChars = 5 }).Should().Be("<reda");
    }

    // 17 ------------------------------------------------------------------------------------------------------------
    [Fact]
    public void A_cut_that_would_split_a_surrogate_pair_ends_before_the_pair_and_the_line_stays_valid_json()
    {
        var shown = CommandLineRedaction.Shown(["a", "\U0001F600"], Limits with { ShownChars = 3 });

        shown.Should().Be("a ", "the pair does not fit: it is left out whole");
        FluentActions.Invoking(() => new UTF8Encoding(false, true).GetBytes(shown)).Should().NotThrow("an unpaired surrogate would not encode");
        JsonEncodedText.Encode(shown).Value.Should().Be("a ", "the JSON encoder refuses an unpaired surrogate, so a status answer can never carry one");
    }

    // 18 ------------------------------------------------------------------------------------------------------------
    [Fact]
    public void Escape_bidi_override_and_a_lone_surrogate_become_question_marks_in_a_shown_argument()
    {
        Shown("x", "a\u001b[31m", "\u202e", "\ud800").Should().Be("x a?[31m ? ?");
    }

    [Fact]
    public void Printable_replaces_every_control_format_and_unpaired_surrogate_character_and_keeps_a_valid_pair()
    {
        ShownText.Printable("a\u001b[31mb\u202ec\ud800d\tz\U0001F600").Should().Be("a?[31mb?c?d?z\U0001F600");
    }

    // 23 ------------------------------------------------------------------------------------------------------------
    [Fact]
    public void An_argument_over_the_judged_limit_is_replaced_whole_without_being_judged()
    {
        Shown("x", new string('a', 5000)).Should().Be("x <redacted>");
    }

    [Fact]
    public void A_pathological_argument_at_the_limit_is_judged_within_the_pattern_ceiling()
    {
        var pathological = new string('a', Limits.MaxJudgedArgChars) + "!";
        var ceiling = TimeSpan.FromMilliseconds(WslCare.Core.Config.Tuning.Default.Int(WslCare.Core.Config.ConfigKeys.Patterns.MatchTimeoutMilliseconds));
        var clock = Stopwatch.StartNew();

        _ = CommandLineRedaction.Shown(["x", pathological, new string('a', Limits.MaxJudgedArgChars - 1) + "!"], Limits);

        clock.Elapsed.Should().BeLessThan(ceiling, "a bounded input keeps every pattern linear: the ceiling is the key's DEFAULT, not its most lenient value");
    }

    // The three paths (plan round, finding 2) --------------------------------------------------------------------------
    [Theory]
    [InlineData("C:\\Program Files (x86)\\Vendor Tool\\tool.exe")]
    [InlineData("/mnt/c/Users/user/\u0424\u0451\u0434\u043e\u0440 \u0418\u0432\u0430\u043d\u043e\u0432/project/run.sh")]
    [InlineData("D:\\dev\\a&b")]
    public void A_path_with_spaces_parentheses_letters_of_any_script_or_an_ampersand_is_shown_whole(string path)
    {
        Shown("tool", path).Should().Be("tool " + path);
    }

    [Theory]
    [InlineData(8176)]
    [InlineData(8486)]
    [InlineData(8592)]
    public void The_captured_telemetry_session_argument_of_a_visual_studio_server_is_redacted_whole(int pid)
    {
        var argv = WslCare.Core.Collectors.Procfs.CommandLineText.Arguments(File.ReadAllBytes(Path.Combine(WslCare.TestSupport.ProcfsFixture.Root, "proc", pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "cmdline")));

        var redacted = CommandLineRedaction.Redact(argv, Limits);

        redacted.Where((_, i) => argv[i].StartsWith("/TelemetrySession:", StringComparison.Ordinal)).Should().Equal(Redacted);
        redacted.Should().NotContain(a => a.Contains("CollectorApiKey", StringComparison.Ordinal));
    }

    // The fixture ------------------------------------------------------------------------------------------------------
    [Fact]
    public void Every_line_of_the_realistic_leaks_fixture_is_shown_as_its_expected_text()
    {
        var cases = FixtureLines();

        cases.Should().HaveCountGreaterThan(15, "the fixture holds a case for every real shape");
        foreach (var (arm, argv, expected) in cases)
        {
            Shown(argv).Should().Be(expected, $"the line of arm {arm}: {string.Join(' ', argv)}");
        }
    }

    [Fact]
    public void No_invented_secret_of_the_fixture_reaches_the_output_in_any_form()
    {
        foreach (var (_, argv, _) in FixtureLines().Where(c => c.Expected.Contains(Redacted, StringComparison.Ordinal)))
        {
            var shown = Shown(argv);
            foreach (var secret in new[] { "Inv3nted", "INVENTEDinvented0123456789", "Pa55w0rd", "invented-collector-value", "inventedvalue", "q7Hn2Vx9" })
            {
                shown.Should().NotContain(secret, $"in {string.Join(' ', argv)}");
            }
        }
    }
}
