using System.Text.RegularExpressions;

namespace WslCare.Core.Processes.Redaction;

/// <summary>What the URL detector could say about a text: it is one, it is none, or it ran out of time and cannot tell — which the rule
/// treats as "redact" (a timeout fails closed).</summary>
internal enum UrlMatch
{
    No,
    Yes,
    Unknown,
}

/// <summary>
/// E15.S7 design §2.1 arm 4: a URL is the one token whose credential can be cut out cleanly, so it is judged FIRST among the shape arms
/// (judge's review, 2026-10-16). Its userinfo — the user name goes with the password — and every secret query or fragment value become
/// <c>&lt;redacted&gt;</c>; then (security review D, 2026-10-18) each PATH SEGMENT and each KEPT query or fragment value is judged by
/// the creds shapes and the entropy test, and a segment that fails is replaced ALONE (<c>…/services/T0/B0/&lt;redacted&gt;</c>): a Slack or
/// Discord webhook, a Telegram bot URL, <c>?t=&lt;random&gt;</c>, <c>#access_token=…</c>. The scheme, host, port and the other segments stay.
/// A creds shape still left in what remains (the host) makes the whole token <c>&lt;redacted&gt;</c>.
/// </summary>
internal static partial class UrlRedaction
{
    private const string Redacted = CommandLineRedaction.Redacted;

    private const string SchemeSeparator = "://";

    /// <summary>Query and fragment parameters that are secret whatever the rest of their name says (<c>X-Amz-Signature</c> is caught by the word).</summary>
    private static readonly string[] SecretParameters = ["sig", "signature", "key", "code", "auth", "access_token", "pass"];

    /// <summary>Whether a text is a URL: a scheme of 2–16 characters and <c>://</c> (a drive path, <c>C:\</c>, is none) — or that the detector
    /// could not decide in time.</summary>
    public static UrlMatch Classify(string text)
    {
        try
        {
            return Scheme().IsMatch(text) ? UrlMatch.Yes : UrlMatch.No;
        }
        catch (RegexMatchTimeoutException)
        {
            return UrlMatch.Unknown;
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9+.-]{1,15}://", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, SecretShapes.PatternCeilingMilliseconds)]
    private static partial Regex Scheme();

    /// <summary>The URL as it may be shown: userinfo cut, secret parameters cut, every other segment and value judged on its own, the
    /// whole token replaced when a creds shape is left in what remains. <paramref name="floor"/> is <c>processes.secretMinChars</c>.</summary>
    public static string Shown(string url, int floor)
    {
        var cut = CutOut(url, floor);
        return SecretShapes.IsCredsShaped(cut) ? Redacted : cut;
    }

    private static string CutOut(string url, int floor)
    {
        var schemeEnd = url.IndexOf(SchemeSeparator, StringComparison.Ordinal) + SchemeSeparator.Length;
        var rest = url[schemeEnd..];
        var authorityEnd = AuthorityEnd(rest);
        return url[..schemeEnd] + WithoutUserinfo(rest[..authorityEnd]) + CutTail(rest[authorityEnd..], floor);
    }

    /// <summary>Where the authority (<c>userinfo@host:port</c>) ends: at the first <c>/</c>, <c>?</c> or <c>#</c>, else at the end.</summary>
    private static int AuthorityEnd(string rest) => rest.IndexOfAny(['/', '?', '#']) is var end and >= 0 ? end : rest.Length;

    /// <summary>The authority with its userinfo (everything up to the LAST <c>@</c>) replaced whole.</summary>
    private static string WithoutUserinfo(string authority) =>
        authority.LastIndexOf('@') is var at and >= 0 ? Redacted + authority[at..] : authority;

    /// <summary>The path, the query and the fragment, each judged: segments one by one, parameters one by one (the fragment's <c>&amp;</c>
    /// list is read like a query).</summary>
    private static string CutTail(string tail, int floor)
    {
        var (beforeFragment, fragment) = SplitAt(tail, '#');
        var (path, query) = SplitAt(beforeFragment, '?');
        return CutPath(path, floor) + CutParameters(query, floor) + CutParameters(fragment, floor);
    }

    /// <summary>The text before the first <paramref name="delimiter"/>, and the rest from the delimiter on (empty when there is none).</summary>
    private static (string Before, string After) SplitAt(string text, char delimiter) =>
        text.IndexOf(delimiter) is var at and >= 0 ? (text[..at], text[at..]) : (text, string.Empty);

    private static string CutPath(string path, int floor) => string.Join('/', path.Split('/').Select(segment => JudgedValue(segment, floor)));

    /// <summary>A part that starts with its delimiter (<c>?</c> or <c>#</c>) and holds an <c>&amp;</c>-separated list; empty stays empty.</summary>
    private static string CutParameters(string part, int floor) =>
        part.Length == 0 ? part : part[0] + string.Join('&', part[1..].Split('&').Select(parameter => CutParameter(parameter, floor)));

    private static string CutParameter(string parameter, int floor) =>
        parameter.IndexOf('=') is var equals and > 0
            ? parameter[..(equals + 1)] + (IsSecretParameter(parameter[..equals]) ? Redacted : JudgedValue(parameter[(equals + 1)..], floor))
            : JudgedValue(parameter, floor);

    /// <summary>A segment or a value that is creds-shaped or high-entropy is replaced by the constant; anything else is kept.</summary>
    private static string JudgedValue(string value, int floor) =>
        SecretShapes.IsCredsShaped(value) || HighEntropy.Looks(value, floor) ? Redacted : value;

    private static bool IsSecretParameter(string name) =>
        SecretShapes.NamesASecret(name) || SecretParameters.Contains(name, StringComparer.OrdinalIgnoreCase);
}
