using System.Text;

namespace WslCare.Core.Processes;

/// <summary>
/// The one command-line string <c>CreateProcessW</c> takes, from an executable and its arguments — quoted so that the C runtime's
/// parser (<c>CommandLineToArgvW</c>, what .NET's <c>Main(string[])</c> reads) gives back EXACTLY those arguments: an argument with a
/// space, a tab or a quote is quoted, a quote inside is escaped, and the backslashes before a quote (or before the closing quote) are
/// doubled. The same rules .NET's own <c>ProcessStartInfo.ArgumentList</c> applies.
/// </summary>
public static class WindowsCommandLine
{
    public static string Of(string executable, IReadOnlyList<string> arguments)
    {
        var line = new StringBuilder();
        Append(line, executable);
        foreach (var argument in arguments)
        {
            line.Append(' ');
            Append(line, argument);
        }

        return line.ToString();
    }

    /// <summary>E15.S7 — STUB (the RED commit): the inverse of <see cref="Of"/>.</summary>
    public static IReadOnlyList<string> Split(string line) => [line];

    /// <summary>E15.S7 — STUB (the RED commit): the Windows binary's entry to the one rule.</summary>
    public static string Shown(string line, Redaction.RedactionLimits limits) => line.Length <= limits.ShownChars ? line : line[..limits.ShownChars];

    private static void Append(StringBuilder line, string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
        {
            line.Append(argument);
            return;
        }

        line.Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            backslashes = AppendChar(line, c, backslashes);
        }

        // The backslashes before the closing quote are doubled, or the last one would escape it.
        line.Append('\\', backslashes * 2).Append('"');
    }

    /// <summary>One character of a quoted argument; the count of backslashes still pending before it.</summary>
    private static int AppendChar(StringBuilder line, char c, int backslashes)
    {
        switch (c)
        {
            case '\\':
                return backslashes + 1;
            case '"':
                line.Append('\\', (backslashes * 2) + 1).Append('"');
                return 0;
            default:
                line.Append('\\', backslashes).Append(c);
                return 0;
        }
    }
}
