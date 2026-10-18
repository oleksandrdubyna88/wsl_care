using WslCare.Core.Collectors.Procfs;

namespace WslCare.Core.Processes.Redaction;

/// <summary>What a view may show of a command line by default: the program, the safe leading arguments, how many there are.
/// Equal when the three are equal AS VALUES — a list inside a record is compared element by element, not by reference.</summary>
public sealed record ShownCommandLine(string Program, IReadOnlyList<string> Arguments, int Total)
{
    public bool Equals(ShownCommandLine? other) =>
        other is not null && Program == other.Program && Total == other.Total && Arguments.SequenceEqual(other.Arguments);

    public override int GetHashCode() => HashCode.Combine(Program, Total, Arguments.Count);
}

/// <summary>
/// E15.S7 design §2.5: <c>program</c>, <c>argumentsShown</c> and <c>argumentsTotal</c> — what the extension's full view shows by
/// default, so a command line stays hidden until a row is revealed. <c>argumentsShown</c> is the longest SAFE PREFIX of the arguments
/// after the program, at most <c>processes.shownSafeArgs</c> of them: the FIRST unsafe argument ends the prefix, so a value after a flag
/// the rule does not know is never shown past the point where doubt starts (<c>claude -p &lt;prompt&gt;</c> shows <c>["-p"]</c> and
/// stops, because a prompt is free text).
/// </summary>
public static class ShownArguments
{
    /// <summary>The program (a file name, <c>.exe</c> stripped, never redacted — a program named after a secret is a residual), the safe
    /// prefix and the count of non-blank arguments. An empty argv falls back to <paramref name="name"/>, the
    /// <c>/proc/[pid]/status</c> name, as <c>Programs</c> does.</summary>
    /// <remarks>The total counts every NON-EMPTY argument after argv[0], whitespace-only ones included (coai code round 3); only the SHOWN
    /// PREFIX skips a whitespace-only argument (node's title padding is a run of spaces and shows nothing a view could use).</remarks>
    public static ShownCommandLine Of(string name, IReadOnlyList<string> argv, IReadOnlyList<string> redacted, RedactionLimits limits, int shownSafeArgs)
    {
        var arguments = argv.Skip(1).Zip(redacted.Skip(1)).Where(pair => pair.First.Length > 0).ToList();
        var candidates = arguments.Where(pair => !string.IsNullOrWhiteSpace(pair.First));
        var safe = candidates.TakeWhile(pair => IsSafe(pair.First, pair.Second, limits)).Take(shownSafeArgs).Select(pair => pair.Second);
        return new(ProgramOf(name, argv), [.. safe], arguments.Count);
    }

    private static string ProgramOf(string name, IReadOnlyList<string> argv) =>
        argv.Count > 0 && CommandLineText.FileNameOf(argv[0]) is { Length: > 0 } program ? program : name;

    /// <summary>An argument is SAFE when the rule left it unchanged, it holds no constant, it is at most the shown cut long, and it is a
    /// flag, a <c>--name=value</c> whose value is safe, a path, a GUID, an npm spec or a plain word shorter than the entropy floor.</summary>
    private static bool IsSafe(string original, string redacted, RedactionLimits limits) =>
        redacted == original
        && !redacted.Contains(CommandLineRedaction.Redacted, StringComparison.Ordinal)
        && redacted.Length <= limits.ShownChars
        && HasASafeShape(original, limits.SecretMinChars);

    private static bool HasASafeShape(string text, int floor) =>
        SafeShapes.IsFlagWithoutValue(text) || IsNamedSafeValue(text, floor) || SafeShapes.IsSafeValue(text, floor);

    /// <summary><c>--name=value</c> whose name is a flag and whose value is a safe value.</summary>
    private static bool IsNamedSafeValue(string text, int floor) =>
        text.IndexOf('=') is var equals and > 0 && SafeShapes.IsFlagWithoutValue(text[..equals]) && SafeShapes.IsSafeValue(text[(equals + 1)..], floor);
}
