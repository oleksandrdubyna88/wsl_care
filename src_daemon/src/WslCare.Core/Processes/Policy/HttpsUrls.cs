using System.Text.RegularExpressions;

namespace WslCare.Core.Processes.Policy;

/// <summary>
/// The ONE rule for an HTTPS address the daemon may send a request to (the clock reference, plan
/// PLAN_windows_time_guard.md D2): read by the configuration key <c>clock.referenceUrl</c> AND by the command slot
/// <see cref="SlotKind.HttpsUrl"/>, so the key cannot accept a value the policy then refuses, nor the reverse.
/// </summary>
/// <remarks>
/// <para><c>https://</c>, a host of ASCII letters, digits, <c>.</c> and <c>-</c> (starting with a letter or digit), an
/// optional port, an optional path of <c>A-Za-z0-9._~/-</c>. No space, quote, <c>=</c>, <c>?</c>, <c>#</c>, <c>@</c>, <c>%</c>
/// or control character: curl reads the value after <c>--url</c>, never as an option, and the never-list's protected-path
/// rule reads the text after a <c>=</c>. The expression is JavaScript-compatible, as every key pattern is (the extension's
/// schema reads it from <c>contracts/config-keys.json</c>).</para>
/// </remarks>
public static class HttpsUrls
{
    /// <summary>The address itself (the slot).</summary>
    public const string Pattern = "^https://[A-Za-z0-9][A-Za-z0-9.-]{0,252}(:[0-9]{1,5})?(/[A-Za-z0-9._~/-]{0,200})?$";

    /// <summary>The key: empty (the HTTP reference is off) or an address.</summary>
    public const string KeyPattern = "^$|" + Pattern;

    public const string Description = "empty, or an https:// address: a host of letters, digits, '.' and '-', an optional port and path (letters, digits, . _ ~ / -); no query, fragment, '=' or space";

    /// <summary>Whether <paramref name="value"/> is an address under <see cref="Pattern"/>, the WHOLE value (.NET's <c>$</c>
    /// also matches before a final newline).</summary>
    public static bool IsAddress(string value) =>
        Regex.Match(value, Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(Config.ConfigKeys.Patterns.MatchTimeoutMilliseconds.Max)) is { Success: true } m
        && m.Index == 0 && m.Length == value.Length;
}
