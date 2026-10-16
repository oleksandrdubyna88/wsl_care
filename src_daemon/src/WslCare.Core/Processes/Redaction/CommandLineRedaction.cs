using WslCare.Core.Collectors.Procfs;

namespace WslCare.Core.Processes.Redaction;

/// <summary>The three numbers the rule needs, handed in as arguments so the rule reads no configuration (as <c>HostRules</c> takes an
/// <c>EffectiveConfig</c>): the shortest token the entropy test judges, the longest argument judged at all, the cut of a shown line.</summary>
public sealed record RedactionLimits(int SecretMinChars, int MaxJudgedArgChars, int ShownChars);

/// <summary>
/// E15.S7 (todo/PLAN_disk_health.md, "E15.S7 design" §2): ONE pure function judges every command line the product shows or stores, on
/// both binaries. No I/O and no configuration read inside. The replacement is always the single constant <see cref="Redacted"/> —
/// never a length, a prefix or a part of the value — and the join is cut AFTER the rule, on a rune boundary, so a cut can only shorten
/// the constant.
/// </summary>
public static class CommandLineRedaction
{
    /// <summary>The only replacement there is.</summary>
    public const string Redacted = "<redacted>";

    /// <summary>The arguments as the view may show them: each judged by <see cref="ArgumentJudge"/> in the context of the argument
    /// before it and the program (the first argument's file name), then made printable.</summary>
    public static IReadOnlyList<string> Redact(IReadOnlyList<string> argv, RedactionLimits limits)
    {
        var program = argv.Count > 0 ? CommandLineText.FileNameOf(argv[0]) : string.Empty;
        return [.. argv.Select((argument, i) => ShownText.Printable(ArgumentJudge.Judge(argument, i > 0 ? argv[i - 1] : string.Empty, program, limits)))];
    }

    /// <summary>The argv as one line: redacted, joined, then cut to <see cref="RedactionLimits.ShownChars"/>.</summary>
    public static string Shown(IReadOnlyList<string> argv, RedactionLimits limits) => Cut(string.Join(' ', Redact(argv, limits)), limits.ShownChars);

    /// <summary>The first <paramref name="chars"/> characters, ending before a surrogate pair the cut would split.</summary>
    internal static string Cut(string line, int chars) =>
        line.Length <= chars ? line : line[..(chars > 0 && char.IsHighSurrogate(line[chars - 1]) ? chars - 1 : chars)];
}
