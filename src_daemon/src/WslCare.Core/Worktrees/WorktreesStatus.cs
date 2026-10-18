using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Thresholds;

namespace WslCare.Core.Worktrees;

/// <summary>Where the section's numbers came from: the scan, when it ended, how old it is at the time of the answer.</summary>
public sealed record ScanBasis(string Source, DateTimeOffset? ScannedAt, double? AgeSeconds);

/// <summary><c>status --json</c>'s <c>worktrees</c> (Windows binary only): the report of the last scan, summarised, with its basis. Not
/// <see cref="Available"/> when there is no usable scan, and then <see cref="Reason"/> says why.</summary>
public sealed record WorktreesSection(bool Available, string? Reason, ScanBasis Basis, bool Stale, IReadOnlyList<string> Roots, WorktreeReport? Report);

/// <summary>
/// What the Windows <c>status</c> says of the worktrees (E19.S3, E19 stories §3 decision 10): the section, and the verdict
/// <c>worktrees.reclaimable</c> — which in this story is ALWAYS <c>unknown</c>. What a cleanup could reclaim is what git confirms
/// (E19.S4); until then no byte figure is called reclaimable, the structural counts ride in the value, and absent is not zero.
/// </summary>
public static class WorktreesStatus
{
    public const string VerdictId = "worktrees.reclaimable";

    public const string NoRoot = "no root configured — choose folders (wsl-care.exe worktrees roots set)";

    internal const double BytesPerGibibyte = 1024d * 1024 * 1024;

    public static (WorktreesSection Section, Verdict Verdict) From(IReadOnlyList<string> configuredRoots, Reading<WorktreeReport> report, WorktreeSettings settings, DateTimeOffset now)
    {
        var limit = $"warn > {settings.ReclaimableWarnGib} GiB once git confirms the candidates";
        var empty = new ScanBasis("scan", null, null);
        if (configuredRoots.Count == 0)
        {
            return (new WorktreesSection(false, NoRoot, empty, false, configuredRoots, null), Unknown(string.Empty, limit, NoRoot, empty));
        }

        return report switch
        {
            Reading<WorktreeReport>.Available scanned => FromScan(configuredRoots, scanned.Value, settings, now, limit),
            _ => NoScan(configuredRoots, report.ReasonOrEmpty, empty, limit),
        };
    }

    private static (WorktreesSection, Verdict) NoScan(IReadOnlyList<string> roots, string why, ScanBasis empty, string limit)
    {
        var reason = $"no scan to read ({why}); run wsl-care.exe worktrees scan";
        return (new WorktreesSection(false, reason, empty, false, roots, null), Unknown(string.Empty, limit, reason, empty));
    }

    private static (WorktreesSection, Verdict) FromScan(IReadOnlyList<string> roots, WorktreeReport report, WorktreeSettings settings, DateTimeOffset now, string limit)
    {
        var age = now - report.ScannedAt;
        var stale = age > TimeSpan.FromDays(settings.ReportMaxAgeDays);
        var basis = new ScanBasis("scan", report.ScannedAt, Math.Max(0, Math.Round(age.TotalSeconds)));
        var reason = stale
            ? $"the last scan is {(int)age.TotalDays} days old (older than worktrees.reportMaxAgeDays = {settings.ReportMaxAgeDays}); {WorktreeReports.GitHasNotConfirmed}"
            : WorktreeReports.GitHasNotConfirmed;
        return (new WorktreesSection(true, null, basis, stale, roots, Summary(report)), Unknown(Structure(report), limit, reason, basis));
    }

    /// <summary>The report without its lists of names: the section is read on every poll.</summary>
    private static WorktreeReport Summary(WorktreeReport report) =>
        report with { Top = [], Roots = [.. report.Roots.Select(r => r with { NamedRetentions = [] })] };

    private static Verdict Unknown(string value, string limit, string reason, ScanBasis basis) =>
        new(VerdictId, Level.Unknown, value, limit, reason) { Basis = new VerdictBasis(VerdictSource.Scan, null, basis.ScannedAt, basis.AgeSeconds) };

    /// <summary>The structural counts, in a person's words — <c>5 worktrees (1 locked), 1 orphan, 4 build-output folders (3.0 GiB present)</c>.</summary>
    private static string Structure(WorktreeReport report)
    {
        var roots = report.Roots;
        var worktrees = roots.Sum(r => r.Worktrees);
        var orphans = roots.Sum(r => r.Orphans);
        var build = roots.Sum(r => r.BuildOutput.All.Count);
        var bytes = roots.Sum(r => r.BuildOutput.All.Bytes);
        return string.Create(CultureInfo.InvariantCulture, $"{Count(worktrees, "worktree")} ({roots.Sum(r => r.Locked)} locked), {Count(orphans, "orphan")}, {Count(build, "build-output folder")} ({bytes / BytesPerGibibyte:0.0} GiB present)");
    }

    private static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? string.Empty : "s")}";
}
