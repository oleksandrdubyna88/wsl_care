using System.Text.RegularExpressions;

namespace WslCare.Core.Processes.Redaction;

/// <summary>
/// E15.S7 design §2, the Windows raw-line BACKSTOP (judge's review, 2026-10-16 — the exact definition). The Windows guarantee is that the
/// rule judges arguments as <c>CommandLineToArgvW</c> splits them; a program that parses its raw line differently (<c>--auth:'my secret
/// value'</c>) can carry a value the split does not isolate. So, after the rule:
/// <list type="number">
/// <item>every match, in the RAW line, of a name holding a secret word followed directly by a separator (<c>:</c>, <c>=</c> or one run of
/// whitespace) is found;</item>
/// <item>its VALUE SPAN starts after the separator — up to the matching close when its first character is a quote
/// <c>CommandLineToArgvW</c> does not honour (<c>'</c> or a backtick), otherwise up to the next whitespace outside a double-quoted run;</item>
/// <item>the span is split on whitespace into words, each stripped of surrounding quotes;</item>
/// <item>the backstop FIRES when any word EQUALS a whole argument of the redacted result — compared as whole strings, never as a
/// substring (the <c>0</c> of <c>--max-thinking-tokens 0</c> sits inside a shown GUID and must not fire).</item>
/// </list>
/// </summary>
internal static partial class RawLineBackstop
{
    private static readonly char[] Quotes = ['\'', '"', '`'];

    /// <summary>Whether a secret the split could not isolate was left shown: the whole shown line must then be redacted.</summary>
    public static bool Fires(string line, IReadOnlyList<string> redacted)
    {
        var shown = redacted.Select(Bare).Where(argument => argument.Length > 0 && argument != CommandLineRedaction.Redacted).ToHashSet(StringComparer.Ordinal);
        try
        {
            return SecretValueSpans(line).SelectMany(Words).Any(shown.Contains);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    /// <summary>A name (letters, digits, <c>_ . -</c>) that starts a run, then a separator: <c>:</c>, <c>=</c> or whitespace. The
    /// lookbehind makes every run be tried once, so the scan is linear in the line.</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_.\-])(?<name>[A-Za-z0-9_.\-]+)(?:[:=]|[ \t]+)", RegexOptions.CultureInvariant, SecretShapes.PatternCeilingMilliseconds)]
    private static partial Regex NameThenSeparator();

    /// <summary>The value spans, in order; a name that sits INSIDE an earlier span (<c>secret</c> in <c>--auth:"my secret value"</c>) is part of
    /// that value, not a second one — scanning resumes after the span.</summary>
    private static IEnumerable<string> SecretValueSpans(string line)
    {
        var until = 0;
        foreach (Match match in NameThenSeparator().Matches(line))
        {
            if (match.Index >= until && SecretShapes.NamesASecret(match.Groups["name"].Value))
            {
                var span = Span(line, match.Index + match.Length);
                until = match.Index + match.Length + span.Length;
                yield return span;
            }
        }
    }

    /// <summary>The value span from <paramref name="start"/>: to the matching close of a quote Windows does not honour, else to the next
    /// whitespace outside a double-quoted run.</summary>
    private static string Span(string line, int start) =>
        start >= line.Length ? string.Empty
        : line[start] is '\'' or '`' ? Quoted(line, start)
        : line[start..EndOfPlainSpan(line, start)];

    private static string Quoted(string line, int start) =>
        line.IndexOf(line[start], start + 1) is var close and >= 0 ? line[start..(close + 1)] : line[start..];

    private static int EndOfPlainSpan(string line, int start)
    {
        var quoted = false;
        var at = start;
        while (at < line.Length && (quoted || !IsSpace(line[at])))
        {
            quoted ^= Toggles(line, at);
            at++;
        }

        return at;
    }

    private static bool Toggles(string line, int at) => line[at] == '"' && !Escaped(line, at);

    private static bool IsSpace(char c) => c is ' ' or '\t';
    /// <summary>A double quote preceded by an odd number of backslashes is a literal character, as the splitter reads it.</summary>
    private static bool Escaped(string line, int quote)
    {
        var backslashes = 0;
        while (quote - backslashes > 0 && line[quote - backslashes - 1] == '\\')
        {
            backslashes++;
        }

        return backslashes % 2 == 1;
    }

    private static IEnumerable<string> Words(string span) =>
        span.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(Bare).Where(word => word.Length > 0);

    private static string Bare(string text) => text.Trim(Quotes);
}
