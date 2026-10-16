using System.Text.RegularExpressions;

namespace WslCare.Core.Processes.Redaction;

/// <summary>
/// E15.S7 design §2.1 arm 4: a URL is the one token whose credential can be cut out cleanly, so it is judged FIRST among the shape arms
/// (judge's review, 2026-10-16). Its userinfo — the user name goes with the password — and every secret query value become
/// <c>&lt;redacted&gt;</c>; the scheme, host, port, path and the other query values stay. The creds-shape test then runs over what
/// REMAINS: a hit there (a <c>ghp_…</c> in a path segment) makes the whole token <c>&lt;redacted&gt;</c>.
/// </summary>
internal static partial class UrlRedaction
{
    private const string Redacted = CommandLineRedaction.Redacted;

    private const string SchemeSeparator = "://";

    /// <summary>Query parameters that are secret whatever the rest of their name says (<c>X-Amz-Signature</c> is caught by the word).</summary>
    private static readonly string[] SecretParameters = ["sig", "signature", "key", "code", "auth", "access_token"];

    /// <summary>A URL: a scheme of 2–16 characters and <c>://</c>. A drive path (<c>C:\</c>) is none.</summary>
    public static bool IsUrl(string text)
    {
        try
        {
            return Scheme().IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9+.-]{1,15}://", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, SecretShapes.PatternCeilingMilliseconds)]
    private static partial Regex Scheme();

    /// <summary>The URL as it may be shown: userinfo and secret query values cut out, the whole token replaced when a creds shape is
    /// left in what remains.</summary>
    public static string Shown(string url)
    {
        var cut = CutOut(url);
        return SecretShapes.IsCredsShaped(cut) ? Redacted : cut;
    }

    private static string CutOut(string url)
    {
        var schemeEnd = url.IndexOf(SchemeSeparator, StringComparison.Ordinal) + SchemeSeparator.Length;
        var rest = url[schemeEnd..];
        var authorityEnd = AuthorityEnd(rest);
        return url[..schemeEnd] + WithoutUserinfo(rest[..authorityEnd]) + CutQuery(rest[authorityEnd..]);
    }

    /// <summary>Where the authority (<c>userinfo@host:port</c>) ends: at the first <c>/</c>, <c>?</c> or <c>#</c>, else at the end.</summary>
    private static int AuthorityEnd(string rest) => rest.IndexOfAny(['/', '?', '#']) is var end and >= 0 ? end : rest.Length;

    /// <summary>The authority with its userinfo (everything up to the LAST <c>@</c>) replaced whole.</summary>
    private static string WithoutUserinfo(string authority) =>
        authority.LastIndexOf('@') is var at and >= 0 ? Redacted + authority[at..] : authority;

    /// <summary>The path, query and fragment with every secret query parameter's value replaced.</summary>
    private static string CutQuery(string tail)
    {
        var start = tail.IndexOf('?');
        if (start < 0)
        {
            return tail;
        }

        var end = tail.IndexOf('#', start) is var hash and >= 0 ? hash : tail.Length;
        return tail[..(start + 1)] + string.Join('&', tail[(start + 1)..end].Split('&').Select(CutParameter)) + tail[end..];
    }

    private static string CutParameter(string parameter) =>
        parameter.IndexOf('=') is var equals and > 0 && IsSecretParameter(parameter[..equals]) ? parameter[..(equals + 1)] + Redacted : parameter;

    private static bool IsSecretParameter(string name) =>
        SecretShapes.NamesASecret(name) || SecretParameters.Contains(name, StringComparer.OrdinalIgnoreCase);
}
