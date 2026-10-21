using FluentAssertions;

using WslCare.Core.Processes.Redaction;

using static WslCare.Core.Tests.Processes.Redaction.RedactionCases;

namespace WslCare.Core.Tests.Processes.Redaction;

/// <summary>E15.S7 §2.5: what a view may show of a command line by DEFAULT — the program, the longest SAFE prefix of its arguments
/// (at most <c>processes.shownSafeArgs</c>), and how many arguments there are. A doubt ends the prefix; nothing past it is shown.</summary>
public sealed class ShownArgumentsTests
{
    private static ShownCommandLine Of(string[] argv, int safeArgs = SafeArgs, string name = "name-of-the-process", RedactionLimits? limits = null) =>
        ShownArguments.Of(name, argv, CommandLineRedaction.Redact(argv, limits ?? Limits), limits ?? Limits, safeArgs);

    [Fact]
    public void A_creds_config_key_shows_the_program_and_the_subcommand_and_counts_the_key()
    {
        var shown = Of(["creds", "config", ConfigKey]);

        shown.Should().Be(new ShownCommandLine("creds", ["config"], 2));
    }

    [Fact]
    public void A_relay_without_arguments_beyond_its_subcommand_shows_it()
    {
        Of(["creds", "relay"]).Should().Be(new ShownCommandLine("creds", ["relay"], 1));
    }

    [Fact]
    public void An_npm_spec_is_a_safe_argument()
    {
        Of(["npm", "exec", "@playwright/mcp@latest"]).Should().Be(new ShownCommandLine("npm", ["exec", "@playwright/mcp@latest"], 2));
    }

    [Fact]
    public void An_agents_command_line_shows_its_first_two_safe_arguments_and_counts_them_all()
    {
        var argv = new[]
        {
            "/home/user/.vscode-server/extensions/vendor.extension-a-1.0.0-linux-x64/resources/native-binary/claude",
            "--output-format", "stream-json", "--verbose", "--input-format", "stream-json", "--max-thinking-tokens", "0", "--permission-prompt-tool", "stdio",
        };

        Of(argv).Should().Be(new ShownCommandLine("claude", ["--output-format", "stream-json"], 9));
    }

    [Fact]
    public void A_flag_with_an_equals_value_that_passed_is_safe()
    {
        Of(["/usr/bin/agy", "--hub", "--hub-port=41790", "--app_data_dir=antigravity", "--csrf_token=" + Blob48])
            .Should().Be(new ShownCommandLine("agy", ["--hub", "--hub-port=41790"], 4));
    }

    [Fact]
    public void A_value_after_a_flag_the_rule_does_not_know_is_never_shown_past_the_point_where_doubt_starts()
    {
        Of(["claude", "-p", "any text that is a prompt, free text"]).Should().Be(new ShownCommandLine("claude", ["-p"], 2));
    }

    [Fact]
    public void A_session_guid_and_a_path_are_safe_arguments()
    {
        Of(["claude", "--resume=" + SessionGuid, "/home/user/git/project-a"]).Should().Be(new ShownCommandLine("claude", ["--resume=" + SessionGuid, "/home/user/git/project-a"], 2));
    }

    [Fact]
    public void The_first_redacted_argument_ends_the_prefix_and_no_shown_argument_holds_the_constant()
    {
        var shown = Of(["tool", "--password", "Inv3ntedPass", "--port", "80"], safeArgs: 8);

        shown.Arguments.Should().Equal("--password");
        shown.Arguments.Should().NotContain(a => a.Contains(Redacted, StringComparison.Ordinal));
        shown.Total.Should().Be(4);
    }

    /// <summary>Deviation from design §2.5, which skips EMPTY arguments only: a whitespace-only argument never enters the SHOWN PREFIX (it shows
    /// nothing a view could use), but <c>argumentsTotal</c> counts it (coai code round 3: the total is every non-empty argument).</summary>
    [Fact]
    public void Empty_and_blank_arguments_never_enter_the_shown_prefix_and_the_total_counts_the_non_empty_ones()
    {
        Of(["node", "", "script.js", "   ", ""]).Should().Be(new ShownCommandLine("node", ["script.js"], 2));
    }

    [Fact]
    public void No_more_than_the_safe_argument_key_allows_are_shown_and_zero_shows_none()
    {
        var argv = new[] { "tool", "a", "b", "c", "d" };

        Of(argv, safeArgs: 3).Arguments.Should().Equal("a", "b", "c");
        Of(argv, safeArgs: 0).Should().Be(new ShownCommandLine("tool", [], 4));
    }

    [Fact]
    public void An_argument_longer_than_the_shown_line_cut_is_unsafe_and_ends_the_prefix()
    {
        Of(["tool", "ok", "/a/long/path/over/the/cut", "x"], limits: Limits with { ShownChars = 10 }).Arguments.Should().Equal(["ok"], "the prefix ends at the first argument over the cut and nothing after it is shown");
    }

    [Fact]
    public void The_program_is_the_file_name_without_exe_and_an_empty_command_line_falls_back_to_the_process_name()
    {
        Of(["C:\\Program Files\\Tool\\claude.exe"]).Program.Should().Be("claude");
        Of([], name: "kworker/u8:3").Should().Be(new ShownCommandLine("kworker/u8:3", [], 0));
    }
}
