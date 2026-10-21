using System.Diagnostics;

using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Worktrees;

/// <summary>
/// The worktree scan (E19.S3, PLAN_disk_hygiene_windows.md §3.3): per root — judged AGAIN, so a hand-edited bad root is refused here with
/// its reason — the kind of every direct child folder (<see cref="FolderKinds"/>, file reads only), and for each worktree and orphan its
/// idleness and its build output (<see cref="FolderWalk"/>). READ-ONLY and process-free: it reaches the disk through
/// <see cref="IFileSystem"/> and starts nothing (git's questions are a later story's, in another file). A walk that runs out of budget,
/// meets an entry it cannot open, or is cut short leaves the folder <see cref="IdleKind.Unknown"/> — never idle — and the candidate retained.
/// </summary>
/// <remarks>
/// <b>Idle</b> is now minus the newest of: every write (files AND folders) found OUTSIDE the folder's build-output folders, each
/// build-output folder's OWN write time (not its inside), the folder's own, and for a worktree its admin folder's <c>HEAD</c>,
/// <c>index</c> and <c>logs/HEAD</c> (git touches <c>index</c> on every status, add and checkout, which agents run constantly). A write dated
/// later than now is never idle. The inside of each build-output folder is walked separately, for its size and its own newest
/// write, so the biggest <c>node_modules</c> cannot spend the budget the other worktrees need.
/// </remarks>
public sealed class WorktreeScanner(IHostPaths paths, IFileSystem files, TimeProvider clock, Func<char, DriveType> driveOf)
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

        return new WorktreeScanResult(started, watch.Elapsed, !budget.RanOut, roots);
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
            walked.Add(walk.Walk(folder));
        }

        return new RootScan(root, string.Empty, facts, walked);
    }
}
