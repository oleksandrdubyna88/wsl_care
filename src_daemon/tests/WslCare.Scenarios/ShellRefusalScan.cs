using System.Text.RegularExpressions;

namespace WslCare.Scenarios;

/// <summary>
/// Finds a shell function that REFUSES — calls <c>refuse</c>, <c>fail</c>, <c>usage_fail</c> or <c>exit &lt;n&gt;</c>, directly or
/// through another such function — being called inside a command substitution <c>$( … )</c>. There the refusal's message is
/// captured into the value and thrown away, and only the exit status survives: release-extension-guard.sh's
/// <c>recorded="$(manifest_field version)"</c> stopped every broken release with exit 1 and an empty log until 2026-10-08.
/// <para>A line scan over the script text, not a shell parser: a function is <c>name() {</c> at column 0 up to the next
/// <c>}</c> at column 0 (or the same line, for a one-line function), comment lines are skipped, and a bare <c>exit</c> (an awk
/// program's own) is not a refusal. It over-approximates — a refusing callee checked with <c>||</c> inside the body still
/// counts — which is the safe direction for a prohibition.</para>
/// </summary>
internal static partial class ShellRefusalScan
{
    /// <summary>One refusing function called inside <c>$( … )</c>: the 1-based line and the function.</summary>
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

    /// <summary>The functions that refuse, directly or through another one — grown to a fixed point.</summary>
    internal static IReadOnlySet<string> RefusingFunctions(IReadOnlyList<string> lines)
    {
        var functions = Functions(lines);
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

    /// <summary>Every call of a refusing function inside <c>$( … )</c>, outside comments.</summary>
    internal static IReadOnlyList<Finding> Findings(IReadOnlyList<string> lines)
    {
        var refusing = RefusingFunctions(lines);
        return [.. lines
            .Select((line, index) => (Line: index + 1, Text: line))
            .Where(l => !IsComment(l.Text))
            .SelectMany(l => Substituted().Matches(l.Text).Select(m => m.Groups["name"].Value).Where(refusing.Contains).Select(name => new Finding(l.Line, name)))];
    }

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

    private static bool Calls(string line, IReadOnlySet<string> functions) =>
        !IsComment(line) && Word().Matches(line).Any(m => functions.Contains(m.Value));

    private static bool IsComment(string line) => line.TrimStart().StartsWith('#');

    [GeneratedRegex(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*)\(\) \{", RegexOptions.CultureInvariant)]
    private static partial Regex Definition();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?:refuse|fail|usage_fail)(?![A-Za-z0-9_])|(?<![A-Za-z0-9_])exit\s+[0-9]", RegexOptions.CultureInvariant)]
    private static partial Regex Refusal();

    [GeneratedRegex(@"\$\(\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex Substituted();

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)]
    private static partial Regex Word();
}
