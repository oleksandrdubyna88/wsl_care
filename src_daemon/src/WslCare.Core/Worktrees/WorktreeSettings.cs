using WslCare.Core.Config;

namespace WslCare.Core.Worktrees;

/// <summary>
/// Every number and list the worktree report is judged by (E19.S3, PLAN_disk_hygiene_windows.md §3.9), read once from the effective
/// configuration — a scan is handed ONE of these, so a test builds a scan over small budgets without a configuration file.
/// </summary>
/// <param name="IdleDays">A folder untouched for this many days is idle (<c>worktrees.idleDays</c>).</param>
/// <param name="ProjectFileExtensions">The extensions of the project files that make a <c>bin</c>/<c>obj</c> beside them build output.</param>
/// <param name="ArtifactPatterns">The file kinds a build writes (<c>worktrees.buildArtifactPatterns</c>, see <see cref="ArtifactKinds"/>).</param>
/// <param name="ArtifactSkewMinutes">A file written this many minutes after the newest compiled output of its folder is the application's, not the build's.</param>
/// <param name="AutomountRoots">The distro's automount prefixes a WSL-made worktree's pointer is translated through (<c>/mnt/</c>).</param>
/// <param name="MaxEntries">The whole scan's entry budget.</param>
/// <param name="MaxSeconds">The whole scan's time budget.</param>
/// <param name="ReportTop">How many folders (and named retentions) the report lists.</param>
/// <param name="MaxRoots">The most roots a list may hold.</param>
/// <param name="ReportMaxAgeDays">A report older than this is stale.</param>
/// <param name="ReclaimableWarnGib">The reclaimable total above which the verdict warns (S4).</param>
/// <param name="MaxSmallFileBytes">The cap of every small file the classifier reads (<c>userFiles.maxSmallFileBytes</c>).</param>
/// <param name="MaxReasonChars">The cap of a lock reason shown (<c>records.maxReasonChars</c>).</param>
public sealed record WorktreeSettings(
    int IdleDays,
    IReadOnlyList<string> ProjectFileExtensions,
    IReadOnlyList<string> ArtifactPatterns,
    int ArtifactSkewMinutes,
    IReadOnlyList<string> AutomountRoots,
    int MaxEntries,
    int MaxSeconds,
    int ReportTop,
    int MaxRoots,
    int ReportMaxAgeDays,
    int ReclaimableWarnGib,
    int MaxSmallFileBytes,
    int MaxReasonChars)
{
    public static WorktreeSettings From(EffectiveConfig config) => new(
        config.Int(ConfigKeys.Worktrees.IdleDays),
        config.TextList(ConfigKeys.Worktrees.ProjectFileExtensions),
        config.TextList(ConfigKeys.Worktrees.BuildArtifactPatterns),
        config.Int(ConfigKeys.Worktrees.ArtifactSkewMinutes),
        config.TextList(ConfigKeys.Worktrees.WslAutomountRoots),
        config.Int(ConfigKeys.Worktrees.ScanMaxEntries),
        config.Int(ConfigKeys.Worktrees.ScanMaxSeconds),
        config.Int(ConfigKeys.Worktrees.ReportTop),
        config.Int(ConfigKeys.Worktrees.MaxRoots),
        config.Int(ConfigKeys.Worktrees.ReportMaxAgeDays),
        config.Int(ConfigKeys.Worktrees.ReclaimableWarnGib),
        config.Int(ConfigKeys.UserFiles.MaxSmallFileBytes),
        config.Int(ConfigKeys.Records.MaxReasonChars));
}
