using System.Text.RegularExpressions;

namespace WslCare.Scenarios;

/// <summary>
/// The built CLI's stderr, sorted into what it carries: the CLI's own messages (a refusal or a note,
/// each ONE line prefixed <c>wsl-care: </c>), the console log lines (<c>[HH:mm:ssZ LVL] source: …</c>,
/// coloured — the family logging rule sends the console sink to stderr because stdout carries
/// answers), and anything else, which a scenario asserts is empty.
/// </summary>
/// <remarks>Measured 2026-10-02 (E1.S3): every run that reaches the machine writes at least its
/// Information request line here, so "a refusal is one stderr line" holds for the MESSAGE, not for the
/// stream. The in-process tests run with a silent logger and could not see this.</remarks>
internal sealed partial record CliStderr(IReadOnlyList<string> Messages, IReadOnlyList<string> LogLines, IReadOnlyList<string> Unexplained)
{
    private const string MessagePrefix = "wsl-care: ";

    public static CliStderr Of(TestSupport.ChildResult result)
    {
        var lines = result.StderrLines.Select(TestSupport.TerminalText.WithoutColour).ToList();
        return new CliStderr(
            [.. lines.Where(l => l.StartsWith(MessagePrefix, StringComparison.Ordinal))],
            [.. lines.Where(l => LogLine().IsMatch(l))],
            [.. lines.Where(l => !l.StartsWith(MessagePrefix, StringComparison.Ordinal) && !LogLine().IsMatch(l))]);
    }

    [GeneratedRegex(@"^\[\d\d:\d\d:\d\dZ (?:VRB|DBG|INF|WRN|ERR|FTL)\] ", RegexOptions.CultureInvariant)]
    private static partial Regex LogLine();
}
