using System.Globalization;

namespace WslCare.Core.Processes.Redaction;

/// <summary>
/// E15.S7 design §3: the ONE definition of "printable" — user or process text echoed into a one-line message, a log, a run record or a
/// status answer. It MOVED here from the CLI's <c>CommandLine</c> (reuse-first, step 2.4: one definition, not a copy) and was widened
/// from control characters to format characters and unpaired surrogates, so a zero-width character, a U+202E right-to-left override
/// (which could make <c>&lt;redacted&gt;</c> read otherwise) or a lone surrogate (which would not encode as UTF-8) cannot reach a screen,
/// a terminal or the JSON of <c>status --json</c>.
/// </summary>
public static class ShownText
{
    /// <summary>The text with every control (<c>Cc</c>: NUL, tab, LF, ESC), format (<c>Cf</c>) and unpaired surrogate (<c>Cs</c>)
    /// character replaced by <c>?</c>; a valid surrogate pair is one character and stays.</summary>
    public static string Printable(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (StartsPair(text, i))
            {
                i++;
            }
            else if (IsUnprintable(chars[i]))
            {
                chars[i] = '?';
            }
        }

        return new string(chars);
    }

    private static bool StartsPair(string text, int index) =>
        char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]);

    private static bool IsUnprintable(char c) =>
        char.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate;
}
