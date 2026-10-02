using System.Text.RegularExpressions;

namespace WslCare.TestSupport;

/// <summary>
/// What a terminal would make of a stream the CLI wrote: the console sink's own colour escapes taken
/// out, and whatever control characters are left over — the ones that would split, repaint or clear
/// the screen rather than colour it.
/// </summary>
/// <remarks>Only colour (SGR, <c>ESC [ … m</c>) is the sink's; a fixture that tests this must use a
/// different sequence (<c>ESC [2J</c> clears the screen), or stripping colour would hide it.</remarks>
public static partial class TerminalText
{
    /// <summary><paramref name="text"/> without the colour escapes the console sink writes.</summary>
    public static string WithoutColour(string text) => Colour().Replace(text, string.Empty);

    /// <summary>Every control character left once colour and line breaks are taken out — empty for a clean stream.</summary>
    public static IReadOnlyList<char> ForeignControlCharacters(string text) =>
        [.. WithoutColour(text).Where(c => char.IsControl(c) && c is not '\r' and not '\n')];

    [GeneratedRegex(@"\x1b\[[0-9;]*m", RegexOptions.CultureInvariant)]
    private static partial Regex Colour();
}
