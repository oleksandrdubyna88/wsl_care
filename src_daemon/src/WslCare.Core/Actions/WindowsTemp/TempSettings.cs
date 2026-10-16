using WslCare.Core.Config;

namespace WslCare.Core.Actions.WindowsTemp;

/// <summary>The numbers and patterns one run of A23 works under — read once from the configuration (every number is a <c>temp.*</c> key).</summary>
/// <param name="AllowGlobs">The name patterns whose top-level entries may be deleted.</param>
/// <param name="OlderThan">An entry is old when its newest descendant is older than this.</param>
/// <param name="ScanMaxNodes">The entries one run may visit.</param>
/// <param name="ScanMaxTime">The time one run may scan.</param>
/// <param name="MaxItemsPerRun">The top-level entries one run may delete.</param>
/// <param name="MaxTopLevelEntries">The most top-level entries one run lists.</param>
public sealed record TempSettings(IReadOnlyList<string> AllowGlobs, TimeSpan OlderThan, int ScanMaxNodes, TimeSpan ScanMaxTime, int MaxItemsPerRun, int MaxTopLevelEntries)
{
    public static TempSettings From(EffectiveConfig config) =>
        new(
            config.TextList(ConfigKeys.Temp.AllowGlobs),
            TimeSpan.FromDays(config.Int(ConfigKeys.Temp.OlderThanDays)),
            config.Int(ConfigKeys.Temp.ScanMaxNodes),
            TimeSpan.FromSeconds(config.Int(ConfigKeys.Temp.ScanMaxSeconds)),
            config.Int(ConfigKeys.Temp.MaxItemsPerRun),
            config.Int(ConfigKeys.Temp.MaxTopLevelEntries));

    /// <summary>The first allow-list pattern that matches <paramref name="name"/>, or empty.</summary>
    public string AllowedBy(string name) => AllowGlobs.FirstOrDefault(g => TempGlob.Matches(g, name)) ?? string.Empty;
}

/// <summary>The names of the figures A23's preview carries in <see cref="ActionPreview.Facts"/>.</summary>
public static class TempFacts
{
    /// <summary>Entries the run would delete.</summary>
    public const string Eligible = "eligible";

    /// <summary>Old, clean entries the allow-list does NOT cover: eligible if the owner allowed them.</summary>
    public const string WouldBeEligible = "wouldBeEligible";

    /// <summary>Entries not on the allow-list (and not shown to be old and clean).</summary>
    public const string NotAllowed = "notAllowed";

    /// <summary>Allowed entries whose newest descendant is newer than the age limit.</summary>
    public const string Young = "young";

    /// <summary>Entries never deleted: <c>claude</c>, another owner, the test-leftover sweep's, a git working folder.</summary>
    public const string Excluded = "excluded";

    /// <summary>Entries retained because they could not be judged: a reparse point, a tree not scanned to its end, an unreadable folder.</summary>
    public const string Unknown = "unknown";

    /// <summary>An interrupted candidate that changed while it lay half deleted.</summary>
    public const string Changed = "changed";

    /// <summary>Entries the budget stopped the run before reaching.</summary>
    public const string Deferred = "deferred";

    /// <summary>Entries this run decided (any verdict).</summary>
    public const string Decided = "decided";

    /// <summary>All top-level entries.</summary>
    public const string Total = "total";

    /// <summary>The prefix of a per-pattern figure: <c>group:coai-*:eligible</c>.</summary>
    public const string GroupPrefix = "group:";
}
