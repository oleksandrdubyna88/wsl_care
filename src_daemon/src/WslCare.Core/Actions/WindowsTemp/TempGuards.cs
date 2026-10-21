using WslCare.Core.Hosting;

namespace WslCare.Core.Actions.WindowsTemp;

/// <summary>
/// The hard "never" rule on a top-level name (PLAN_windows_runner.md §3.12, the owner's rule of 2026-10-02): the folder Claude Code
/// keeps under <c>%TEMP%</c> is never selected, whatever the allow-list says, checked BEFORE any traversal. The name is compared
/// ordinally ignoring case, and the canonical full path is compared against <c>&lt;root&gt;\claude</c> — a trailing separator, a
/// trailing dot or space (which Win32 strips), another case or an absolute spelling all name the same folder.
/// </summary>
public static class TempGuards
{
    private static readonly char[] Stripped = ['\\', '/', ' ', '.'];

    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>Whether <paramref name="spelling"/> names the folder Claude Code keeps directly under <paramref name="root"/>.</summary>
    public static bool IsClaude(PathRules rules, string root, string spelling) =>
        spelling.TrimEnd(Stripped) is { Length: > 0 } trimmed && (IsBareName(trimmed) || rules.PathEquals(trimmed, rules.Join(root, TempGlob.ClaudeName)));

    private static bool IsBareName(string trimmed) =>
        trimmed.IndexOfAny(Separators) < 0 && string.Equals(trimmed, TempGlob.ClaudeName, StringComparison.OrdinalIgnoreCase);
}
