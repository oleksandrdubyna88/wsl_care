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
/// <c>worktrees.reclaimable</c>: what a cleanup would be allowed to delete — the candidates that pass every check including git's
/// (E19.S4), as <c>a24</c> + <c>a24Orphans</c>. <c>warn</c> above <c>worktrees.reclaimableWarnGib</c>; <c>unknown</c> with no root, no usable or a stale
/// scan, a scan that had no git to ask, or a figure below the warning that a budget or a git failure may have understated — never <c>ok</c> on a
/// lower bound, and absent is not zero.
/// </summary>
public static class WorktreesStatus
{
    public const string VerdictId = "worktrees.reclaimable";

    public const string NoRoot = "no root configured — choose folders (wsl-care.exe worktrees roots set)";

    internal const double BytesPerGibibyte = 1024d * 1024 * 1024;

    public static (WorktreesSection Section, Verdict Verdict) From(IReadOnlyList<string> configuredRoots, Reading<WorktreeReport> report, WorktreeSettings settings, DateTimeOffset now)
    {
        var limit = $"warn > {settings.ReclaimableWarnGib} GiB confirmed by git";
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
        var section = new WorktreesSection(true, null, basis, stale, roots, Summary(report));
        var reclaimable = report.Reclaimable;
        if (!reclaimable.Available)
        {
            return (section, Unknown(Structure(report), limit, StaleOr(stale, age, settings, reclaimable.Reason), basis));
        }

        return (section, Confirmed(report, reclaimable, settings, stale, age, limit, basis));
    }

    private static string StaleOr(bool stale, TimeSpan age, WorktreeSettings settings, string reason) =>
        stale ? $"the last scan is {(int)age.TotalDays} days old (older than worktrees.reportMaxAgeDays = {settings.ReportMaxAgeDays}); {reason}" : reason;

    /// <summary>The verdict of a scan git answered: the two parts in the value, the reasons things stay in the reason.</summary>
    private static Verdict Confirmed(WorktreeReport report, ReclaimableReport reclaimable, WorktreeSettings settings, bool stale, TimeSpan age, string limit, ScanBasis basis)
    {
        var (worktrees, orphans) = (reclaimable.A24 ?? new Tally(0, 0), reclaimable.A24Orphans ?? new Tally(0, 0));
        var bytes = Saturated([worktrees.Bytes, orphans.Bytes]);
        var value = string.Create(CultureInfo.InvariantCulture, $"{bytes / BytesPerGibibyte:0.0} GiB: {worktrees.Bytes / BytesPerGibibyte:0.0} GiB in {Count(worktrees.Count, "worktree folder")} + {orphans.Bytes / BytesPerGibibyte:0.0} GiB in {Count(orphans.Count, "orphan folder")} (tracked files of orphans not checked); {Structure(report)}");
        var retained = reclaimable.Retained ?? [];
        var kept = retained.Count == 0 ? "nothing else was found" : "kept: " + string.Join("; ", retained.Select(r => $"{r.Reason} ({r.Count})"));
        var needed = reclaimable.NothingNeededGit ? "nothing needed git: every candidate is kept by another check, so git confirmed nothing and was asked nothing" : string.Empty;
        var warns = bytes > (long)settings.ReclaimableWarnGib * (long)BytesPerGibibyte;
        var doubt = !report.Complete ? "the scan did not finish, so the figure is a lower bound" : reclaimable.NotAsked > 0 ? $"{Count(reclaimable.NotAsked, "candidate")} could not be asked of git, so the figure may be understated" : string.Empty;
        var level = stale ? Level.Unknown : warns ? Level.Warn : doubt.Length > 0 ? Level.Unknown : Level.Ok;
        var reason = string.Join("; ", new[] { stale ? StaleOr(true, age, settings, string.Empty).TrimEnd(' ', ';') : string.Empty, doubt, needed, kept }.Where(p => p.Length > 0));
        return new Verdict(VerdictId, level, value, limit, reason) { Basis = new VerdictBasis(VerdictSource.Scan, null, basis.ScannedAt, basis.AgeSeconds) };
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
        var worktrees = roots.Sum(r => (long)r.Worktrees);
        var orphans = roots.Sum(r => (long)r.Orphans);
        var build = roots.Sum(r => (long)r.BuildOutput.All.Count);
        var bytes = Saturated(roots.Select(r => r.BuildOutput.All.Bytes));
        return string.Create(CultureInfo.InvariantCulture, $"{Count(worktrees, "worktree")} ({roots.Sum(r => (long)r.Locked)} locked), {Count(orphans, "orphan")}, {Count(build, "build-output folder")} ({bytes / BytesPerGibibyte:0.0} GiB present)");
    }

    /// <summary>The sum of byte figures read back from a file a person or a crash may have made: it stops at <see cref="long.MaxValue"/> instead of
    /// throwing, so one crafted figure never takes the whole status down (the counts are ints summed in long and cannot overflow).</summary>
    private static long Saturated(IEnumerable<long> figures) =>
        figures.Aggregate(0L, static (sum, next) => next > 0 && sum > long.MaxValue - next ? long.MaxValue : sum + Math.Max(0, next));

    private static string Count(long count, string noun) => $"{count} {noun}{(count == 1 ? string.Empty : "s")}";
}
