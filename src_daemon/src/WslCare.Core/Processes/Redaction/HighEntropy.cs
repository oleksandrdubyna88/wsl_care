namespace WslCare.Core.Processes.Redaction;

/// <summary>
/// E15.S7 design §2.4 (arm 8): the test for a bare token no list knows. It runs only on an argument that reached it — so no path, no
/// URL, no assignment and no flag value — and it asks four things of the token: long enough (<c>processes.secretMinChars</c>, an
/// argument of the rule), in a credential alphabet, mixed enough, random enough. A GUID is exempt (a Claude session id); a digest is
/// NOT (decided 2026-10-15: a pre-<c>ghp_</c> GitHub token is 40 hex, and a hex-encoded key of any length looks exactly like one).
/// </summary>
internal static class HighEntropy
{
    /// <summary>Random base62 of 20 characters measures 4.1–4.3 bits per character; <c>ubuntu-24.04-20260101</c> about 3.3.</summary>
    private const double MinEntropyBits = 3.5;

    /// <summary>Character classes present among lowercase, UPPERCASE, digits and the symbols <c>+ / = _ -</c>.</summary>
    private const int MinClasses = 3;

    /// <summary>One letter case plus digits needs this share of digits (a 32-hex Twilio-shaped token has about 40 %; a CamelCase
    /// identifier has two classes and none).</summary>
    private const int MinDigitPercent = 30;

    private const int Percent = 100;

    private const string Symbols = "+/=_-";

    /// <summary>Whether <paramref name="token"/> looks like a credential nothing else recognised.</summary>
    public static bool Looks(string token, int minChars) =>
        token.Length >= minChars && !IsExempt(token) && LooksRandom(token.TrimEnd('='));

    /// <summary>A GUID (a session id) and a flag name (<c>--sourceGeneratorExecutionPreference</c>: its dashes are a symbol class and its
    /// CamelCase two more, so it would pass for a credential) are exempt; a flag is no VALUE, and the rule judges what follows it.</summary>
    private static bool IsExempt(string token) => SafeShapes.IsGuid(token) || SafeShapes.IsFlagWithoutValue(token);

    private static bool LooksRandom(string body) => InAlphabet(body) && Mixed(body) && Bits(body) >= MinEntropyBits;

    /// <summary>Every character is in <c>[A-Za-z0-9+/=_-]</c>, a trailing run of <c>=</c> (base64 padding) already stripped.</summary>
    private static bool InAlphabet(string body) => body.Length > 0 && body.All(IsCredentialCharacter);

    private static bool IsCredentialCharacter(char c) => char.IsAsciiLetterOrDigit(c) || Symbols.Contains(c);

    /// <summary>At least three classes, or one letter case plus digits with the digits at least <see cref="MinDigitPercent"/> %.</summary>
    private static bool Mixed(string body)
    {
        var lower = body.Any(char.IsAsciiLetterLower);
        var upper = body.Any(char.IsAsciiLetterUpper);
        var digits = body.Count(char.IsAsciiDigit);
        var symbols = body.Any(c => Symbols.Contains(c));
        var classes = new[] { lower, upper, digits > 0, symbols }.Count(present => present);
        return classes >= MinClasses || ((lower ^ upper) && digits * Percent >= body.Length * MinDigitPercent);
    }

    /// <summary>Shannon entropy over the characters, in bits per character.</summary>
    public static double Bits(string body) =>
        body.Length == 0 ? 0 : -body.GroupBy(c => c).Select(g => (double)g.Count() / body.Length).Sum(p => p * Math.Log2(p));
}
