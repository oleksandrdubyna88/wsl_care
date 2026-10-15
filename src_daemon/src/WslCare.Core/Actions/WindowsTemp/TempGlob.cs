namespace WslCare.Core.Actions.WindowsTemp;

/// <summary>
/// The one rule for a <c>temp.allowGlobs</c> member (PLAN_windows_runner.md §3.12): a name pattern with <c>*</c> and <c>?</c>, no
/// path separator, compared ignoring case — and never one that could select the folder Claude Code keeps under the temp directory
/// or the test-leftover sweep's names. The key's validation and the selection ask the SAME matcher, so what the loader refuses and
/// what the action would match cannot disagree.
/// </summary>
public static class TempGlob
{
    /// <summary>The folder Claude Code keeps under <c>%TEMP%</c> — never cleaned (the owner's rule of 2026-10-02).</summary>
    public const string ClaudeName = "claude";

    /// <summary>The prefix of the test harness' leftovers, which the test-leftover sweep owns (PLAN_disk_hygiene_windows.md §6): this
    /// action never selects such a name.</summary>
    public const string TestLeftoverPrefix = "testsToDelete-";

    /// <summary>The shape of a member: letters, digits, <c>. _ -</c> and the two wildcards; no separator, no space.</summary>
    public const string Pattern = "^[A-Za-z0-9._*?-]{1,64}$";

    private const int MaxLength = 64;

    public const string Description = "a file-name pattern of at most 64 letters, digits, '.', '_', '-', '*' and '?' (no path separator), starting with at least two literal characters, that matches neither \"claude\" nor any name starting with \"testsToDelete-\"";

    /// <summary>Whether <paramref name="name"/> matches <paramref name="pattern"/> as a whole, ignoring case.</summary>
    public static bool Matches(string pattern, string name)
    {
        int p = 0, n = 0, star = -1, resume = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || SameLetter(pattern[p], name[n])))
            {
                p++;
                n++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                resume = n;
            }
            else if (star >= 0)
            {
                p = star + 1;
                n = ++resume;
            }
            else
            {
                return false;
            }
        }

        return pattern[p..].All(c => c == '*');
    }

    private static bool SameLetter(char a, char b) => char.ToUpperInvariant(a) == char.ToUpperInvariant(b);

    /// <summary>Why <paramref name="pattern"/> is refused; empty when it may be a member.</summary>
    public static string Problem(string pattern) => pattern switch
    {
        _ when pattern.Length is 0 or > MaxLength || pattern.Any(IsOutsideTheAlphabet) => $"not {Description}",
        _ when !StartsWithLiterals(pattern) => $"\"{pattern}\" must start with at least two literal characters (not * or ?): a pattern that opens with a wildcard names far too much",
        _ when Matches(pattern, ClaudeName) => $"\"{pattern}\" would match \"{ClaudeName}\", the folder Claude Code keeps under the temp directory, which is never cleaned",
        _ when CouldMatchWithPrefix(pattern, TestLeftoverPrefix) => $"\"{pattern}\" could match a name starting with {TestLeftoverPrefix} (the test-leftover sweep's), which this cleanup never takes",
        _ => string.Empty,
    };

    private static bool StartsWithLiterals(string pattern) => pattern.Length >= 2 && pattern[0] is not ('*' or '?') && pattern[1] is not ('*' or '?');

    /// <summary>Whether SOME name that starts with <paramref name="prefix"/> matches <paramref name="pattern"/> — exact, not sampled: such a name
    /// exists exactly when some leading part of the pattern matches the whole prefix (whatever follows can then be spelled literally).</summary>
    private static bool CouldMatchWithPrefix(string pattern, string prefix) =>
        Enumerable.Range(1, pattern.Length).Any(length => Matches(pattern[..length], prefix));

    private static bool IsOutsideTheAlphabet(char c) => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '*' or '?');
}
