using System.Text.RegularExpressions;

namespace WslCare.Core.Processes.Redaction;

/// <summary>
/// E15.S7 design §2.1 arm 6 and §2.5: the shapes a command-line argument may have and be SHOWN — a path, a GUID, an npm spec, a flag
/// without a value, a plain word. Shared by the rule (a path is shown whole) and by <see cref="ShownArguments"/> (what a view shows by
/// default), so the two cannot disagree about what "a path" is. Every pattern is anchored and bounded by its input.
/// </summary>
internal static partial class SafeShapes
{
    private const RegexOptions Options = RegexOptions.CultureInvariant;

    /// <summary>A PATH (design §2.1 arm 6): starts with <c>/</c>, <c>./</c>, <c>../</c>, <c>~/</c>, <c>\\</c> or a drive, and holds only
    /// letters and digits of ANY script plus space and <c>._~+@%:/\-()[],'&amp;</c>. So <c>C:\Program Files (x86)\…</c>, a folder named
    /// in Cyrillic and <c>D:\dev\a&amp;b</c> are paths, and <c>/TelemetrySession:{"CollectorApiKey":…}</c> is not — it holds <c>{</c>
    /// and a double quote. Paths carry the programs, scripts and folders a view needs; a secret used as a file NAME is a residual.</summary>
    public static bool IsPath(string text) => Match(PathShape(), text);

    [GeneratedRegex(@"^(?:/|\./|\.\./|~/|\\\\|[A-Za-z]:[\\/])[\p{L}\p{N} ._~+@%:/\\()\[\],'&-]*\z", Options, SecretShapes.PatternCeilingMilliseconds)]
    private static partial Regex PathShape();

    /// <summary>A GUID — a Claude session id (<c>--resume=&lt;guid&gt;</c>, <c>-r &lt;guid&gt;</c>).</summary>
    public static bool IsGuid(string text) => Match(Guid(), text);

    [GeneratedRegex(@"^[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\z", Options, SecretShapes.PatternCeilingMilliseconds)]
    private static partial Regex Guid();

    /// <summary>An npm spec — <c>@playwright/mcp@latest</c>.</summary>
    public static bool IsNpmSpec(string text) => Match(NpmSpec(), text);

    [GeneratedRegex(@"^@?[a-z0-9][a-z0-9._-]*(?:/[a-z0-9][a-z0-9._-]*)?@[a-z0-9.+-]+\z", Options, SecretShapes.PatternCeilingMilliseconds)]
    private static partial Regex NpmSpec();

    /// <summary>A flag with no value: <c>-x</c>, <c>--name</c>.</summary>
    public static bool IsFlagWithoutValue(string text) => Match(Flag(), text);

    [GeneratedRegex(@"^--?[A-Za-z0-9][A-Za-z0-9._-]*\z", Options, SecretShapes.PatternCeilingMilliseconds)]
    private static partial Regex Flag();

    /// <summary>A plain word of <c>[A-Za-z0-9._@:+-]</c> shorter than <paramref name="maxExclusive"/> (the entropy floor).</summary>
    public static bool IsPlainWord(string text, int maxExclusive) => text.Length < maxExclusive && Match(Plain(), text);

    [GeneratedRegex(@"^[A-Za-z0-9._@:+-]+\z", Options, SecretShapes.PatternCeilingMilliseconds)]
    private static partial Regex Plain();

    /// <summary>A value a view may show after <c>--name=</c> or on its own: a path, a GUID, an npm spec or a plain word.</summary>
    public static bool IsSafeValue(string text, int maxExclusive) =>
        IsPath(text) || IsGuid(text) || IsNpmSpec(text) || IsPlainWord(text, maxExclusive);

    /// <summary>One match; a match that ran out of time is NOT a safe shape — a view shows less, never more.</summary>
    private static bool Match(Regex pattern, string text)
    {
        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
