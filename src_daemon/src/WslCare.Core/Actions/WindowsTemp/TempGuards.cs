using WslCare.Core.Hosting;

namespace WslCare.Core.Actions.WindowsTemp;

/// <summary>The hard "never" rules on a top-level name — STUB.</summary>
public static class TempGuards
{
    /// <summary>Whether <paramref name="spelling"/> names the folder Claude Code keeps directly under <paramref name="root"/>.</summary>
    public static bool IsClaude(PathRules rules, string root, string spelling) => false;
}
