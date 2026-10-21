namespace WslCare.Core.Worktrees;

/// <summary>
/// Makes <c>worktrees.json</c> of a scan (E19.S3, PLAN_disk_hygiene_windows.md §3.3 "The report"): counts per kind with the flags beside
/// them, idle, build output by where it lies, what could not be established and why, and the largest folders BY NAME — never their
/// contents. Pure.
/// </summary>
/// <remarks>
/// <b>Build output</b> <c>all</c> is every build-output folder the walk found, whatever became of it; <c>inIdleUnlocked</c>,
/// <c>inIdleLocked</c> and <c>inOrphans</c> hold only those that passed every STRUCTURAL check (not retained), by where they lie — and
/// which of them a cleanup would delete is git's word (E19.S4), so nothing here is called reclaimable: <see cref="ReclaimableReport"/>
/// says <c>available: false</c> and why, and absent is not zero.
/// </remarks>
public static class WorktreeReports
{
    public const string GitHasNotConfirmed = "git has not confirmed the candidates yet (E19.S4)";

    public static WorktreeReport From(WorktreeScanResult result, WorktreeSettings settings) =>
        new(
            Core.SchemaVersion.Current,
            result.ScannedAt,
            result.Elapsed.TotalSeconds,
            result.Complete,
            [.. result.Roots.Select(root => RootOf(root, settings))],
            Top(result, settings),
            new ReclaimableReport(false, GitHasNotConfirmed));

    private static RootReport RootOf(RootScan root, WorktreeSettings settings)
    {
        var facts = root.Facts;
        var walked = root.Walked;
        var idle = walked.Where(f => f.Idle.Kind == IdleKind.Idle).ToList();
        return new RootReport(
            root.Path,
            root.Refused.Length > 0 ? root.Refused : null,
            facts.Count,
            facts.Count(f => f.Kind == FolderKind.Worktree),
            facts.Count(f => f.Locked),
            facts.Count(f => f.Kind == FolderKind.Orphan),
            facts.Count(f => f.Kind == FolderKind.ForeignPointer),
            facts.Count(f => f.Kind == FolderKind.MainClone),
            facts.Count(f => f.Kind == FolderKind.NotGit),
            facts.Count(f => f.Kind == FolderKind.Link),
            facts.Count(f => f.TestLeftover),
            walked.Sum(f => f.Bytes),
            new IdleOver(settings.IdleDays, idle.Count, idle.Sum(f => f.Bytes)),
            BuildOutputOf(walked),
            UnknownOf(root, settings),
            NamedRetentions(walked).Take(settings.ReportTop).ToList());
    }

    private static BuildOutputTotals BuildOutputOf(IReadOnlyList<FolderScan> walked)
    {
        var all = walked.SelectMany(f => f.Candidates).ToList();
        var eligible = walked.Select(f => (Folder: f, Fit: f.Candidates.Where(c => c.Retained.Length == 0).ToList())).ToList();
        return new BuildOutputTotals(
            Tally(all),
            Tally(eligible.Where(e => e.Folder.Facts.Kind == FolderKind.Worktree && e.Folder.Idle.Kind == IdleKind.Idle && !e.Folder.Facts.Locked).SelectMany(e => e.Fit)),
            Tally(eligible.Where(e => e.Folder.Facts.Kind == FolderKind.Worktree && e.Folder.Idle.Kind == IdleKind.Idle && e.Folder.Facts.Locked).SelectMany(e => e.Fit)),
            Tally(eligible.Where(e => e.Folder.Facts.Kind == FolderKind.Orphan).SelectMany(e => e.Fit)),
            [.. all.Where(c => c.Retained.Length > 0).GroupBy(c => BuildOutputRule.GroupOf(c.Retained)).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new RetainedGroup(g.Key, g.Count(), g.Sum(c => c.Bytes)))]);
    }

    private static Tally Tally(IEnumerable<BuildCandidate> candidates)
    {
        var list = candidates.ToList();
        return new Tally(list.Count, list.Sum(c => c.Bytes));
    }

    /// <summary>The folders no walk established — a walk that ran out of budget or met an entry it could not open — and any whose pointer could
    /// not be read, with the distinct reasons (at most the report's top).</summary>
    private static UnknownTotals UnknownOf(RootScan root, WorktreeSettings settings)
    {
        var reasons = root.Walked.Where(f => f.Idle.Kind == IdleKind.Unknown).Select(f => f.Idle.Reason)
            .Concat(root.Facts.Where(f => f.Kind == FolderKind.Unreadable).Select(f => f.Why))
            .ToList();
        return new UnknownTotals(reasons.Count, string.Join("; ", reasons.Distinct(StringComparer.Ordinal).Take(settings.ReportTop)));
    }

    private static IEnumerable<NamedRetention> NamedRetentions(IReadOnlyList<FolderScan> walked) =>
        walked.OrderBy(f => f.Facts.Name, StringComparer.OrdinalIgnoreCase)
            .SelectMany(f => f.Candidates.Where(c => c.Retained.Length > 0).OrderBy(c => c.Relative, StringComparer.OrdinalIgnoreCase).Select(c => new NamedRetention(f.Facts.Name, c.Relative, c.Retained)));

    private static IReadOnlyList<TopFolder> Top(WorktreeScanResult result, WorktreeSettings settings) =>
    [
        .. result.Roots.SelectMany(root => root.Walked.Select(f => (Root: root.Path, Folder: f)))
            .OrderByDescending(t => t.Folder.Candidates.Sum(c => c.Bytes))
            .ThenByDescending(t => t.Folder.Bytes)
            .ThenBy(t => t.Folder.Facts.Name, StringComparer.OrdinalIgnoreCase)
            .Take(settings.ReportTop)
            .Select(t => new TopFolder(t.Root, t.Folder.Facts.Name, KindName(t.Folder.Facts.Kind), t.Folder.Facts.Locked, IdleName(t.Folder.Idle.Kind), t.Folder.Candidates.Sum(c => c.Bytes), t.Folder.Bytes)),
    ];

    private static string KindName(FolderKind kind) => char.ToLowerInvariant(kind.ToString()[0]) + kind.ToString()[1..];

    private static string IdleName(IdleKind kind) => kind.ToString().ToLowerInvariant();
}
