using System.Reflection;
using System.Text.RegularExpressions;

using FluentAssertions;

using WslCare.Core.Processes;
using WslCare.Core.Processes.Redaction;

using static WslCare.Core.Tests.Processes.Redaction.RedactionCases;

namespace WslCare.Core.Tests.Processes.Redaction;

/// <summary>E15.S7 after the Fable security review and the coai code round: each hole the reviewers MEASURED is a test that was red against the
/// first build — a flag-shaped token, a trailing <c>=</c>, a Windows <c>/switch:value</c>, the tails of a URL, a secret word followed by
/// a SPACE inside a script, attached short options, a Unicode line separator, a timeout that failed open, a Windows caller that could skip
/// the backstop. Every value is invented.</summary>
public sealed partial class SecurityReviewTests
{
    private const string Telegram = "bot" + "123456789" + ":" + "AAHq7Hn2Vx9LmT4bKd8RzY3wPc6Jf1sGa5";

    // A — the flag exemption was a hole --------------------------------------------------------------------------------
    [Theory]
    [InlineData("-")]
    [InlineData("--")]
    public void A_token_that_merely_starts_with_dashes_is_redacted_and_never_shown_by_either_view(string dashes)
    {
        var argv = new[] { "tool", dashes + Blob48 };

        Shown(argv).Should().Be("tool <redacted>");
        ShownArguments.Of("tool", argv, CommandLineRedaction.Redact(argv, Limits), Limits, SafeArgs).Arguments.Should().BeEmpty();
    }

    // B — a trailing '=' hid a token behind an assignment whose NAME was never judged -------------------------------------
    [Theory]
    [InlineData("q7Hn2Vx9LmT4bKd8RzY3wPc6Jf1sGa5UeN0oXiBlZrM=")]
    [InlineData(Hex40 + "=")]
    [InlineData(Blob48 + "=1")]
    public void A_token_followed_by_an_equals_sign_is_redacted_whole_whatever_follows_it(string argument)
    {
        Shown("tool", argument).Should().Be("tool <redacted>");
    }

    // C — Windows switches: /name:value passed as a path ---------------------------------------------------------------
    [Fact]
    public void A_windows_switch_holding_a_password_is_redacted_and_the_other_switches_stay()
    {
        Shown("cmdkey", "/add:srv", "/user:alice", "/password:Hunter2Invented").Should().Be("cmdkey /add:srv /user:alice <redacted>");
    }

    [Fact]
    public void A_windows_switch_holding_a_token_is_redacted_and_the_view_stops_before_it()
    {
        var argv = new[] { "tool", "/token:" + Blob48 };

        Shown(argv).Should().Be("tool <redacted>");
        ShownArguments.Of("tool", argv, CommandLineRedaction.Redact(argv, Limits), Limits, SafeArgs).Arguments.Should().BeEmpty();
    }

    [Fact]
    public void A_secret_windows_switch_given_without_a_value_redacts_the_next_argument()
    {
        Shown("tool", "/password", "Hunter2Invented").Should().Be("tool /password <redacted>");
    }

    [Fact]
    public void The_windows_command_line_of_cmdkey_is_redacted_through_the_one_windows_entry()
    {
        WindowsCommandLine.Shown("cmdkey.exe /add:srv /user:alice /password:Hunter2Invented", Limits).Should().Be("cmdkey.exe /add:srv /user:alice <redacted>");
    }

    // D — the tails of a URL --------------------------------------------------------------------------------------------
    [Fact]
    public void A_creds_shaped_or_random_path_segment_of_a_url_is_replaced_alone_and_its_neighbours_stay()
    {
        Shown("curl", "https://hooks.slack.example/services/T0000000/B0000000/q7Hn2Vx9LmT4bKd8RzY3wPc6")
            .Should().Be("curl https://hooks.slack.example/services/T0000000/B0000000/<redacted>");
        Shown("curl", "https://discord.example/api/webhooks/123456789012345678/" + Blob48 + Blob48[..20])
            .Should().Be("curl https://discord.example/api/webhooks/123456789012345678/<redacted>");
        Shown("curl", "https://files.example.invalid/" + GitHubToken + "/a.tgz").Should().Be("curl https://files.example.invalid/<redacted>/a.tgz");
    }

    [Fact]
    public void A_telegram_bot_token_in_the_path_of_a_url_is_redacted_alone()
    {
        Shown("curl", "https://api.telegram.example/" + Telegram + "/sendMessage").Should().Be("curl https://api.telegram.example/<redacted>/sendMessage");
    }

    [Fact]
    public void A_password_parameter_named_pass_and_a_random_query_value_are_redacted_and_the_other_values_stay()
    {
        Shown("curl", "https://x.example/login?user=alice&pass=Hunter2Invented").Should().Be("curl https://x.example/login?user=alice&pass=<redacted>");
        Shown("curl", "https://x.example/dl?t=" + Blob48 + "&n=2").Should().Be("curl https://x.example/dl?t=<redacted>&n=2");
    }

    [Fact]
    public void A_fragment_is_judged_like_a_query_so_an_access_token_in_it_goes()
    {
        var shown = Shown("open", "https://app.example/cb#access_token=" + Blob48 + "&state=ok");

        shown.Should().Be("open https://app.example/cb#access_token=<redacted>&state=ok");
    }

    // E — a secret word followed by SPACE inside a script ---------------------------------------------------------------
    [Fact]
    public void A_script_run_through_a_shell_that_holds_a_secret_word_and_a_space_is_redacted_whole()
    {
        Shown("sh", "-c", "tool --password Hunter2Invented --port 5").Should().Be("sh -c <redacted>");
        Shown("bash", "-lc", "sshpass -p Hunter2Invented ssh host").Should().Be("bash -lc <redacted>");
    }

    [Fact]
    public void A_free_text_prompt_holding_a_secret_word_is_redacted_whole_which_is_the_recorded_cost()
    {
        Shown("claude", "-p", "explain the token flow").Should().Be("claude -p <redacted>");
    }

    // G — U+2028 / U+2029 ------------------------------------------------------------------------------------------------
    [Fact]
    public void The_unicode_line_and_paragraph_separators_become_question_marks()
    {
        ShownText.Printable("a\u2028b\u2029c").Should().Be("a?b?c");
    }

    // coai 0 — a regex timeout fails CLOSED -------------------------------------------------------------------------------
    [Fact]
    public void Every_timeout_handler_of_the_rule_answers_closed_a_hit_or_the_undecided_url_never_a_plain_false()
    {
        var folder = Path.Combine(SourceRoot(), "WslCare.Core", "Processes", "Redaction");
        var offenders = Directory.EnumerateFiles(folder, "*.cs")
            .Where(file => !file.EndsWith("SafeShapes.cs", StringComparison.Ordinal))
            .SelectMany(file => TimeoutHandlers().Matches(File.ReadAllText(file)).Select(m => (File: Path.GetFileName(file), Body: m.Groups["body"].Value)))
            .Where(handler => !handler.Body.Contains("return true", StringComparison.Ordinal) && !handler.Body.Contains("Unknown", StringComparison.Ordinal))
            .Select(handler => handler.File)
            .ToList();

        offenders.Should().BeEmpty("a match that ran out of time must redact (SafeShapes alone answers false: a shape not proved is a shape not shown)");
    }

    [GeneratedRegex(@"catch \(RegexMatchTimeoutException\)\s*\{(?<body>[^}]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex TimeoutHandlers();

    // coai 1 — a Windows caller cannot skip the backstop -------------------------------------------------------------------
    [Fact]
    public void The_windows_split_is_not_public_so_the_one_windows_entry_is_the_only_way_to_a_windows_shown_line()
    {
        typeof(WindowsCommandLine).GetMethod(nameof(WindowsCommandLine.Split), BindingFlags.Public | BindingFlags.Static).Should().BeNull();
    }

    [Fact]
    public void No_product_code_outside_the_rule_splits_a_windows_line_itself()
    {
        var root = SourceRoot();
        var allowed = new[] { "WindowsCommandLine.cs", "WindowsArgv.cs" };
        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}Redaction{Path.DirectorySeparatorChar}") && !allowed.Contains(Path.GetFileName(file)))
            .Where(file => File.ReadAllText(file) is var text && (text.Contains("WindowsCommandLine.Split(", StringComparison.Ordinal) || text.Contains("WindowsArgv.Split(", StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .ToList();

        offenders.Should().BeEmpty("a Windows line is rendered by WindowsCommandLine.Shown, whose backstop is not optional");
    }

    private static string SourceRoot() =>
        typeof(SecurityReviewTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "WslCare.SourceRoot").Value!;

    // coai 3 (argumentsTotal counts every non-empty argument) is ShownArgumentsTests.Empty_and_blank_arguments_never_enter_the_shown_prefix…

    // coai 2 — attached short-option credentials ---------------------------------------------------------------------------
    [Fact]
    public void A_curl_user_and_an_sshpass_password_attached_to_their_option_are_redacted_and_the_option_stays()
    {
        Shown("curl", "-uuser:Pa55w0rdInvented", "https://x.example/").Should().Be("curl -u<redacted> https://x.example/");
        Shown("sshpass", "-pHunter2Invented", "ssh", "host").Should().Be("sshpass -p<redacted> ssh host");
    }

}
