using System.Diagnostics;

using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Worktrees;

/// <summary>
/// The worktree scan (E19.S3, PLAN_disk_hygiene_windows.md §3.3): per root — judged AGAIN, so a hand-edited bad root is refused here with
/// its reason — the kind of every direct child folder (<see cref="FolderKinds"/>, file reads only), and for each worktree and orphan its
/// idleness and its build output (<see cref="FolderWalk"/>). READ-ONLY and process-free: it reaches the disk through
/// <see cref="IFileSystem"/>; the ONLY process it can start is git's three read-only questions (E19.S4), through the <see cref="GitQuestions"/> it is
/// handed (null: none — nothing is then confirmed). A walk that runs out of budget,
/// meets an entry it cannot open, or is cut short leaves the folder <see cref="IdleKind.Unknown"/> — never idle — and the candidate retained.
/// </summary>
/// <remarks>
/// <b>Idle</b> is now minus the newest of: every write (files AND folders) found OUTSIDE the folder's build-output folders, each
/// build-output folder's OWN write time (not its inside), the folder's own, and for a worktree its admin folder's <c>HEAD</c>,
/// <c>index</c> and <c>logs/HEAD</c> (git touches <c>index</c> on every status, add and checkout, which agents run constantly). A write dated
/// later than now is never idle. The inside of each build-output folder is walked separately, for its size and its own newest
/// write, so the biggest <c>node_modules</c> cannot spend the budget the other worktrees need.
/// </remarks>
public sealed class WorktreeScanner(IHostPaths paths, IFileSystem files, TimeProvider clock, Func<char, DriveType> driveOf, GitQuestions? git = null)
{
    public WorktreeScanResult Run(IReadOnlyList<string> configuredRoots, WorktreeSettings settings, CancellationToken cancellationToken, Action<string>? progress = null)
    {
        var started = clock.GetUtcNow();
        var watch = Stopwatch.StartNew();
        var budget = new ScanBudget(settings.MaxEntries, TimeSpan.FromSeconds(settings.MaxSeconds), watch);
        var verdicts = WorktreeRoots.Judge(paths, files, driveOf, settings.MaxRoots, configuredRoots);
        var roots = new List<RootScan>();
        for (var i = 0; i < verdicts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var where = $"worktrees scan: root {i + 1} of {verdicts.Count}";
            progress?.Invoke($"{where}: {verdicts[i].Path}");
            var share = budget.Share(verdicts.Count - i);
            var scanned = verdicts[i].Accepted
                ? ScanRoot(verdicts[i].Path, settings, share, started, cancellationToken)
                : new RootScan(configuredRoots[i], $"{verdicts[i].Rule}: {verdicts[i].Reason}", [], []);
            roots.Add(scanned);
            budget.Settle(share);
            progress?.Invoke(Ended(where, scanned));
        }

        return new WorktreeScanResult(started, watch.Elapsed, !budget.RanOut, roots) { GitAsked = git is not null };
    }

    /// <summary>The line a root's end gets on the progress channel (coai code round: a scan can take minutes, and a silent one looks hung).</summary>
    private static string Ended(string where, RootScan scanned) =>
        scanned.Refused.Length > 0 ? $"{where} refused: {scanned.Refused}" : $"{where} done: {scanned.Facts.Count} folders, {scanned.Walked.Count} walked";

    private RootScan ScanRoot(string root, WorktreeSettings settings, ScanBudget budget, DateTimeOffset now, CancellationToken cancellationToken) =>
        files.ListEntries(root, new ListingBounds(settings.MaxEntries, () => budget.OutOfTime, cancellationToken)) switch
        {
            EntryListing.Unreadable unreadable => new RootScan(root, $"unreadable: {unreadable.Reason}", [], []),
            EntryListing.Listed listed => ScanFolders(root, listed, settings, budget, now, cancellationToken),
            _ => throw new UnreachableException("EntryListing is a closed set"),
        };

    /// <summary>The folder's candidates that passed every structural check, with git's word on them — asked only where the folder is otherwise
    /// eligible (idle, and unlocked for a worktree) and the budget has time left; every other candidate keeps <see cref="CandidateGit.NotAsked"/>,
    /// because the first reason a candidate stays is the one the report names. One batch of questions per folder, the repository being its working directory.</summary>
    private FolderScan WithGit(FolderScan folder, string root, ScanBudget budget, CancellationToken cancellationToken)
    {
        var fit = folder.Candidates.Where(c => c.Retained.Length == 0).ToList();
        if (git is null || fit.Count == 0 || folder.Idle.Kind != IdleKind.Idle || folder.Facts.Locked || budget.OutOfTime)
        {
            return folder;
        }

        var answers = git.Ask(new GitQuestion(folder.Facts.Path, [.. fit.Select(c => c.Relative)], root, GitFolderOf(folder.Facts)), cancellationToken);
        var byName = answers.ToDictionary(a => a.Name, a => a.Verdict, StringComparer.OrdinalIgnoreCase);
        return folder with { Candidates = [.. folder.Candidates.Select(c => byName.TryGetValue(c.Relative, out var verdict) ? c with { Git = Outcome(verdict) } : c)] };
    }

    private static CandidateGit Outcome(GitVerdict verdict) => verdict switch
    {
        GitVerdict.Confirmed confirmed => new CandidateGit.Confirmed(confirmed.TrackedFilesChecked),
        GitVerdict.Retained retained => new CandidateGit.Retained(retained.Reason),
        _ => throw new UnreachableException("GitVerdict is a closed set"),
    };

    /// <summary>A worktree: git must name the folder itself as the top level. An orphan: the repository its admin folder lay in — the folder
    /// two levels above <c>&lt;repository&gt;.gitworktrees&lt;name&gt;</c>, read from the classifier's own value (never from anywhere else);
    /// none when the admin folder is not shaped so, and git is then not started.</summary>
    private GitFolder GitFolderOf(FolderFacts facts) => facts.Kind == FolderKind.Orphan
        ? new GitFolder.Orphan(OrphanRepository(facts.AdminFolder))
        : new GitFolder.Repository(facts.Path);

    /// <summary>The repository folder an orphan's admin folder (<c>.gitworktrees&lt;name&gt;</c>) belongs to; empty when it is not shaped so.</summary>
    internal string OrphanRepository(string adminFolder)
    {
        var worktrees = adminFolder.Length == 0 ? string.Empty : paths.Rules.Parent(adminFolder);
        return worktrees.Length > 0 && paths.Rules.Segments(worktrees) is { Count: > 0 } segments && segments[^1].Equals("worktrees", StringComparison.OrdinalIgnoreCase)
            ? paths.Rules.Parent(worktrees)
            : string.Empty;
    }

    private RootScan ScanFolders(string root, EntryListing.Listed listed, WorktreeSettings settings, ScanBudget budget, DateTimeOffset now, CancellationToken cancellationToken)
    {
        budget.SpendListing(listed.Entries.Count);
        if (!listed.Complete)
        {
            budget.MarkRanOut();
        }

        var facts = listed.Entries
            .Where(e => e.Kind is EntryKind.Directory or EntryKind.Link)
            .Select(e => FolderKinds.Classify(files, paths, settings, root, e))
            .ToList();
        var walk = new FolderWalk(paths, files, settings, budget, now, cancellationToken);
        var walked = new List<FolderScan>();
        foreach (var folder in facts.Where(f => f.Kind is FolderKind.Worktree or FolderKind.Orphan))
        {
            cancellationToken.ThrowIfCancellationRequested();
            walked.Add(WithGit(walk.Walk(folder), root, budget, cancellationToken));
        }

        return new RootScan(root, string.Empty, facts, walked);
    }
}
