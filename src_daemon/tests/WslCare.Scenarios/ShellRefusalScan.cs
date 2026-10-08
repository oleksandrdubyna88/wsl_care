using System.Text.RegularExpressions;

namespace WslCare.Scenarios;

/// <summary>
/// Finds a shell function that REFUSES — calls <c>refuse</c>, <c>fail</c>, <c>usage_fail</c> or <c>exit &lt;n&gt;</c>, directly or
/// through another such function — being called inside a command substitution <c>$( … )</c>. There the refusal's message is
/// captured into the value and thrown away, and only the exit status survives: release-extension-guard.sh's
/// <c>recorded="$(manifest_field version)"</c> stopped every broken release with exit 1 and an empty log until 2026-10-08.
/// <para>A text scan, not a shell parser. A function is <c>name() {</c> (or <c>name(){</c>, <c>function name {</c>) at column 0
/// up to the next <c>}</c> at column 0 (or the same line, for a one-line function); comment lines are skipped; a bare <c>exit</c> (an awk program's own) is not a refusal.
/// The refusing set is computed over ALL the scripts given, because scripts source each other's libraries (<c>lib/*.sh</c>):
/// a function defined in one file and called in another counts. A call site is <c>$(</c> followed by the function's name with
/// only whitespace or line continuations between — across lines — and is reported at the line the <c>$(</c> is on. It
/// over-approximates (a refusing callee checked with <c>||</c> inside a body still counts; two files' functions of one name
/// are merged), which is the safe direction for a prohibition.</para>
/// <para>WHAT IT DOES NOT SEE (no shipped script uses any of these; each would need a shell parser): a refusing call that is
/// not the FIRST command of the substitution (<c>$(a; fn)</c>, <c>$(a | fn)</c>, inside an <c>if</c> in it), one behind an
/// assignment prefix (<c>$(MODE=x fn)</c>), a comment line between <c>$(</c> and the command, backtick substitutions, and a
/// function defined other than at column 0. A green run is a statement about the shapes above, not about every shell
/// construct.</para>
/// </summary>
internal static partial class ShellRefusalScan
{
    /// <summary>One refusing function called inside <c>$( … )</c>: the 1-based line of its <c>$(</c> and the function.</summary>
    internal sealed record Finding(int Line, string Function);

    /// <summary>Every function the script defines, by name, with its body's lines.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> Functions(IReadOnlyList<string> lines)
    {
        var functions = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Count; i++)
        {
            var definition = Definition().Match(lines[i]);
            if (definition.Success)
            {
                functions[definition.Groups["name"].Value] = Body(lines, i);
            }
        }

        return functions;
    }

    /// <summary>The functions that refuse across all <paramref name="scripts"/>, directly or through another one — grown to a
    /// fixed point.</summary>
    internal static IReadOnlySet<string> RefusingFunctions(IEnumerable<IReadOnlyList<string>> scripts)
    {
        var functions = scripts
            .SelectMany(script => Functions(script))
            .GroupBy(f => f.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.SelectMany(f => f.Value).ToList(), StringComparer.Ordinal);
        var refusing = functions.Where(f => f.Value.Any(l => !IsComment(l) && Refusal().IsMatch(l))).Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var grown = true;
        while (grown)
        {
            var next = functions.Where(f => !refusing.Contains(f.Key) && f.Value.Any(l => Calls(l, refusing))).Select(f => f.Key).ToList();
            refusing.UnionWith(next);
            grown = next.Count > 0;
        }

        return refusing;
    }

    /// <summary>The refusing functions of one script on its own.</summary>
    internal static IReadOnlySet<string> RefusingFunctions(IReadOnlyList<string> lines) => RefusingFunctions([lines]);

    /// <summary>Every call of a <paramref name="refusing"/> function inside <c>$( … )</c> in this script, outside comments.</summary>
    internal static IReadOnlyList<Finding> Findings(IReadOnlyList<string> lines, IReadOnlySet<string> refusing)
    {
        var text = string.Join('\n', lines);
        return [.. Substituted().Matches(text)
            .Select(m => (Line: LineOf(text, m.Index), Name: m.Groups["name"].Value))
            .Where(m => refusing.Contains(m.Name) && !IsComment(lines[m.Line - 1]))
            .Select(m => new Finding(m.Line, m.Name))];
    }

    /// <summary>The script's findings against its own refusing functions.</summary>
    internal static IReadOnlyList<Finding> Findings(IReadOnlyList<string> lines) => Findings(lines, RefusingFunctions(lines));

    private static List<string> Body(IReadOnlyList<string> lines, int start)
    {
        if (lines[start].TrimEnd().EndsWith('}'))
        {
            return [lines[start]];
        }

        var end = start + 1;
        while (end < lines.Count && !lines[end].StartsWith('}'))
        {
            end++;
        }

        return [.. lines.Skip(start + 1).Take(end - start - 1)];
    }

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    private static bool Calls(string line, IReadOnlySet<string> functions) =>
        !IsComment(line) && Word().Matches(line).Any(m => functions.Contains(m.Value));

    private static bool IsComment(string line) => line.TrimStart().StartsWith('#');

    /// <summary><c>name() {</c>, <c>name(){</c>, <c>function name {</c> or <c>function name() {</c> — at column 0, so an awk
    /// program's indented <c>function f(x) {</c> inside a script is not read as a shell function.</summary>
    [GeneratedRegex(@"^(?:function[ \t]+(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:[ \t]*\(\))?|(?<name>[A-Za-z_][A-Za-z0-9_]*)[ \t]*\(\))[ \t]*\{", RegexOptions.CultureInvariant)]
    private static partial Regex Definition();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?:refuse|fail|usage_fail)(?![A-Za-z0-9_])|(?<![A-Za-z0-9_])exit\s+[0-9]", RegexOptions.CultureInvariant)]
    private static partial Regex Refusal();

    /// <summary><c>$(</c>, then whitespace — newlines included — or <c>\</c>-continuations, then the command's name.</summary>
    [GeneratedRegex(@"\$\((?:\s|\\\n)*(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex Substituted();

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)]
    private static partial Regex Word();
}
