namespace WslCare.Core.Health;

/// <summary>What the advice recommends: <c>memory=</c> and <c>swap=</c> in GB (0 swap = no swap line).</summary>
public sealed record WslConfigRecommendation(int MemoryGb, int SwapGb);

/// <summary>One advised line: the section WSL reads it from, the line to put there, what the file says now, the measurement it
/// rests on.</summary>
public sealed record WslConfigAdviceLine(string Section, string Line, string Now, string Basis);

/// <summary>
/// E14 S5: the <c>.wslconfig</c> advice — SHOWN, NEVER WRITTEN (owner question Q5). One line per setting the file does not
/// already say (compared without case), each in its section (<c>autoMemoryReclaim</c> is <c>[experimental]</c>'s), each with
/// what the file says now and the measurement it rests on. PURE.
/// </summary>
public static class WslConfigAdvice
{
    public const string NotSet = "not set";

    public const string MemoryBasis = "the VM's ceiling the owner recommends (wslConfig.recommendedMemoryGb); research/2026-10-02_wsl_resource_baseline.md";

    public const string SwapBasis = "the 2026-10-07 evening used 9.9 of 12 GB of swap (research/2026-10-07_evening_overload.md, L3); wslConfig.recommendedSwapGb";

    public const string ReclaimBasis = "without it WSL keeps the page cache it never hands back (research/2026-10-02_wsl_resource_baseline.md); gradual hangs with systemd and Docker Desktop";

    /// <summary>The recommendation the configuration holds (<c>wslConfig.recommendedMemoryGb</c>, <c>wslConfig.recommendedSwapGb</c>).</summary>
    public static WslConfigRecommendation Configured =>
        new(Config.Tuning.Current.Int(Config.ConfigKeys.WslConfig.RecommendedMemoryGb), Config.Tuning.Current.Int(Config.ConfigKeys.WslConfig.RecommendedSwapGb));

    /// <summary>Every advised line the file does not already say, in the order WSL documents them.</summary>
    public static IReadOnlyList<WslConfigAdviceLine> For(WslConfigAudit audit, WslConfigRecommendation recommended)
    {
        if (!audit.Read)
        {
            // Own code review 2026-10-09: a file that exists but was not read may say all three — no advice about what was not read.
            return [];
        }

        var settings = audit.Present ? audit.Settings : new WslConfigSettings(string.Empty, string.Empty, string.Empty, string.Empty);
        IEnumerable<(string Section, string Key, string Value, string Now, string Basis)> wanted =
        [
            ("wsl2", "memory", $"{recommended.MemoryGb}GB", settings.Memory, MemoryBasis),
            .. recommended.SwapGb > 0 ? [("wsl2", "swap", $"{recommended.SwapGb}GB", settings.Swap, SwapBasis)] : Array.Empty<(string, string, string, string, string)>(),
            ("experimental", "autoMemoryReclaim", "dropcache", settings.AutoMemoryReclaim, ReclaimBasis),
        ];
        return [.. wanted
            .Where(w => !string.Equals(w.Now, w.Value, StringComparison.OrdinalIgnoreCase))
            .Select(w => new WslConfigAdviceLine(w.Section, $"{w.Key}={w.Value}", w.Now.Length > 0 ? w.Now : NotSet, w.Basis))];
    }
}
