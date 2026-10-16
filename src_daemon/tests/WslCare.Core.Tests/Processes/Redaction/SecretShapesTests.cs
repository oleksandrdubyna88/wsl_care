using System.Diagnostics;

using FluentAssertions;

using WslCare.Core.Config;
using WslCare.Core.Processes.Redaction;

using static WslCare.Core.Tests.Processes.Redaction.RedactionCases;

namespace WslCare.Core.Tests.Processes.Redaction;

/// <summary>E15.S7 design §2.2–§2.4, one level below the rule: the closed lists a command line is judged by — the secret words, the creds
/// shapes, the blob test, the entropy test, the program-specific flags — each held directly, so a wrong list is named before a wrong
/// verdict is.</summary>
public sealed class SecretShapesTests
{
    [Theory]
    [InlineData("--password")]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("--max-thinking-tokens")]
    [InlineData("AWS_SECRET_ACCESS_KEY")]
    [InlineData("--csrf_token")]
    [InlineData("--Authorization")]
    [InlineData("db-passwd")]
    [InlineData("--client-secret")]
    [InlineData("PRIVATE_KEY")]
    [InlineData("--passphrase")]
    [InlineData("--cookie")]
    [InlineData("--creds")]
    [InlineData("--pwd")]
    [InlineData("--bearer")]
    [InlineData("--key")]
    [InlineData("API_KEY")]
    [InlineData("-key")]
    [InlineData("x.key")]
    public void A_name_holding_a_secret_word_or_the_whole_word_key_names_a_secret(string name)
    {
        SecretShapes.NamesASecret(name).Should().BeTrue();
    }

    [Theory]
    [InlineData("--keyboard")]
    [InlineData("--keep")]
    [InlineData("--monkey")]
    [InlineData("--session")]
    [InlineData("--resume")]
    [InlineData("--id")]
    [InlineData("--port")]
    [InlineData("--add-dir")]
    [InlineData("--hub-port")]
    public void A_name_without_a_secret_word_and_without_the_whole_word_key_names_none(string name)
    {
        SecretShapes.NamesASecret(name).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(CredsFamilies), MemberType = typeof(RedactionCases))]
    public void Each_creds_family_is_a_creds_shape_inside_a_longer_text_and_at_a_token_boundary(string family, string token)
    {
        SecretShapes.IsCredsShaped(token).Should().BeTrue(family);
        SecretShapes.IsCredsShaped("Authorization: token " + token).Should().BeTrue($"{family} after a space");
        SecretShapes.IsCredsShaped("--flag=" + token).Should().BeTrue($"{family} after an equals sign");
    }

    [Theory]
    [InlineData("task-queue-with-a-long-enough-name-12345")]
    [InlineData("risk-assessment-of-the-whole-pipeline")]
    [InlineData("hf_short")]
    [InlineData("--print-the-version")]
    [InlineData("/home/user/git/project-a/src/Program.cs")]
    public void Ordinary_text_and_a_prefix_inside_a_word_are_no_creds_shape(string text)
    {
        SecretShapes.IsCredsShaped(text).Should().BeFalse();
    }

    [Theory]
    [InlineData("{\"CollectorApiKey\":\"x\"}", true)]
    [InlineData("Authorization: Basic dXNlcjpwYXNz", true)]
    [InlineData("const token='x'; run(token)", true)]
    [InlineData("Server=db;Password=x", true)]
    [InlineData("{ \"key\": \"x\" }", true)]
    [InlineData("const net = require('net'); const client = net.createConnection({ host: '127.0.0.1', port: 41304 })", false)]
    [InlineData("tokenizer", false)]
    [InlineData("a plain sentence without any of the words", false)]
    public void A_blob_is_judged_by_a_secret_word_followed_by_a_separator(string text, bool expected)
    {
        SecretShapes.HoldsASecretBeforeASeparator(text).Should().Be(expected);
    }

    [Theory]
    [InlineData("curl", "-u", "alice:Pa55w0rd", true)]
    [InlineData("wget", "--user", "alice:Pa55w0rd", true)]
    [InlineData("git", "--proxy-user", "alice:Pa55w0rd", true)]
    [InlineData("curl", "-u", "alice", false)]
    [InlineData("systemd", "--user", "alice:Pa55w0rd", false)]
    [InlineData("sshpass", "-p", "Inv3ntedPass", true)]
    [InlineData("mysql", "-p", "Inv3ntedPass", true)]
    [InlineData("docker", "-p", "8080:80", false)]
    [InlineData("curl", "-p", "8080:80", false)]
    [InlineData("SSHPASS.exe", "-p", "x", false)]
    public void A_program_specific_value_flag_marks_its_next_argument(string program, string previous, string argument, bool expected)
    {
        SecretShapes.IsProgramPasswordValue(program, previous, argument).Should().Be(expected);
    }

    [Theory]
    [InlineData("mysql", "-pInv3ntedPass", true)]
    [InlineData("mariadb-dump", "-pInv3ntedPass", true)]
    [InlineData("mysql", "-p", false)]
    [InlineData("mysql", "--protocol=tcp", false)]
    [InlineData("mysql", "-P3306", false)]
    [InlineData("docker", "-p8080:80", false)]
    public void An_attached_password_is_the_mysql_familys_dash_p_with_more_after_it(string program, string argument, bool expected)
    {
        SecretShapes.IsAttachedPassword(program, argument).Should().Be(expected);
    }

    [Fact]
    public void The_pattern_ceiling_compiled_into_every_pattern_is_the_range_maximum_of_the_key()
    {
        SecretShapes.PatternCeilingMilliseconds.Should().Be(ConfigKeys.Patterns.MatchTimeoutMilliseconds.Max, "an attribute takes a constant: the bootstrap ceiling TextRule.Matching uses");
    }

    [Fact]
    public void Every_pattern_answers_a_pathological_input_of_the_judged_limit_within_the_keys_default_ceiling()
    {
        var pathological = new string('a', Limits.MaxJudgedArgChars) + "!";
        var ceiling = TimeSpan.FromMilliseconds(Tuning.Default.Int(ConfigKeys.Patterns.MatchTimeoutMilliseconds));
        var clock = Stopwatch.StartNew();

        _ = SecretShapes.IsCredsShaped(pathological);
        _ = SecretShapes.HoldsASecretBeforeASeparator(pathological);
        _ = SecretShapes.NamesASecret(pathological);
        _ = SecretShapes.IsUserinfo(pathological);
        _ = HighEntropy.Looks(pathological, Limits.SecretMinChars);

        clock.Elapsed.Should().BeLessThan(ceiling);
    }

    [Theory]
    [InlineData(Blob48)]
    [InlineData(Hex32)]
    [InlineData(Hex40)]
    [InlineData(Hex64)]
    [InlineData("q7Hn2Vx9LmT4bKd8RzY3wPc6Jf1sGa5UeN0oXiBlZrMt4Kd9==")]
    public void A_long_mixed_random_looking_token_is_high_entropy(string token)
    {
        HighEntropy.Looks(token, Limits.SecretMinChars).Should().BeTrue();
    }

    [Theory]
    [InlineData("6f1c2b7e-3a94-4d05-8c1e-9b2a7d4f5e60", "a GUID is a session id")]
    [InlineData("sourceGeneratorExecutionPreference", "CamelCase: two classes and no digits")]
    [InlineData("ubuntu-24.04-20260101", "dots are outside the credential alphabet")]
    [InlineData("correct-horse-battery-staple", "two classes, no digits")]
    [InlineData("aaaaaaaaaaaaaaaaaaaa1111111111", "mixed but not random")]
    [InlineData("rovodev-ide-vscode", "under the floor")]
    [InlineData("20260101120000-20260101120000", "digits and symbols are no letters")]
    [InlineData("@playwright/mcp@latest-and-greater", "an npm spec holds @")]
    [InlineData("--sourceGeneratorExecutionPreference", "a flag name is no value: its dashes are a symbol class, its CamelCase two more")]
    public void A_name_a_version_a_guid_or_a_phrase_is_not_high_entropy(string token, string because)
    {
        HighEntropy.Looks(token, Limits.SecretMinChars).Should().BeFalse(because);
    }

    [Fact]
    public void The_entropy_floor_is_an_argument_not_a_constant()
    {
        var token = Blob48[..19];

        HighEntropy.Looks(token, 20).Should().BeFalse();
        HighEntropy.Looks(token, 19).Should().BeTrue();
    }

    [Fact]
    public void The_captured_invented_tokens_measure_above_the_entropy_floor_of_three_and_a_half_bits()
    {
        foreach (var token in new[] { Blob48, Hex32, Hex40, Hex64 })
        {
            HighEntropy.Bits(token).Should().BeGreaterThanOrEqualTo(3.5, token);
        }

        HighEntropy.Bits("ubuntu-24.04-20260101").Should().BeLessThan(3.5);
    }
}
