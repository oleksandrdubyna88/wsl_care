using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Actions.WindowsTemp;

/// <summary>What scanning one top-level entry's tree found — a closed set.</summary>
internal abstract record TreeScan
{
    private TreeScan()
    {
    }

    /// <summary>The tree was scanned to its end.</summary>
    /// <param name="Newest">The newest write time of anything inside it, folders included, and its own.</param>
    /// <param name="NewestFile">The newest write time of any FILE inside it.</param>
    /// <param name="Bytes">The summed length of its files (logical bytes).</param>
    /// <param name="HoldsGit">A <c>.git</c> file or folder sits at its top.</param>
    /// <param name="NewestFolderCreated">The newest CREATION time of any folder inside it (not its own).</param>
    public sealed record Measured(DateTimeOffset Newest, DateTimeOffset NewestFile, long Bytes, bool HoldsGit, DateTimeOffset NewestFolderCreated) : TreeScan;

    /// <summary>The tree could not be judged — a reparse point inside, the budget, an unreadable folder.</summary>
    public sealed record Retained(string Reason) : TreeScan;
}

/// <summary>
/// The bounded walk of one candidate's tree (PLAN_windows_runner.md §3.12): the newest descendant's write time over files AND
/// folders, never following a reparse point — a reparse entry met anywhere retains the tree — and never past the run's budget.
/// Every listing is bounded, and a listing that stopped early is "not scanned to its end".
/// </summary>
internal static class TempScanner
{
    public static TreeScan Scan(IFileSystem files, PathRules rules, string folder, DateTimeOffset own, TempBudget budget, CancellationToken token)
    {
        var state = new Walk(own);
        var pending = new Stack<string>([folder]);
        while (pending.Count > 0)
        {
            var at = pending.Pop();
            if (Step(files, rules, folder, at, state, pending, budget, token) is { Length: > 0 } stop)
            {
                return new TreeScan.Retained(stop);
            }
        }

        return new TreeScan.Measured(state.Newest, state.NewestFile, state.Bytes, state.HoldsGit, state.NewestFolderCreated);
    }

    /// <summary>One folder: listed within the budget, its entries taken in. Empty when it was taken in; otherwise why the tree is retained.</summary>
    private static string Step(IFileSystem files, PathRules rules, string top, string at, Walk state, Stack<string> pending, TempBudget budget, CancellationToken token)
    {
        var listing = files.ListEntries(at, new ListingBounds(budget.Remaining, budget.OutOfTime, token));
        if (listing is not EntryListing.Listed { Complete: true } listed)
        {
            return listing is EntryListing.Listed cut ? $"not scanned to its end within the budget ({cut.Note})" : ((EntryListing.Unreadable)listing).Reason;
        }

        budget.Spend(listed.Entries.Count);
        foreach (var entry in listed.Entries)
        {
            if (Take(entry, rules.Join(at, entry.Name), at == top, state, pending) is { Length: > 0 } reparse)
            {
                return reparse;
            }
        }

        return string.Empty;
    }

    private static string Take(FileEntry entry, string path, bool atTop, Walk state, Stack<string> pending)
    {
        state.HoldsGit |= atTop && entry.Name == ".git";
        switch (entry.Kind)
        {
            case EntryKind.Link:
                return $"it holds a reparse point ({entry.Name}), which is never followed";
            case EntryKind.Directory:
                state.Newest = Max(state.Newest, entry.LastWriteUtc);
                state.NewestFolderCreated = Max(state.NewestFolderCreated, entry.CreatedUtc);
                pending.Push(path);
                return string.Empty;
            default:
                state.Newest = Max(state.Newest, entry.LastWriteUtc);
                state.NewestFile = Max(state.NewestFile, entry.LastWriteUtc);
                state.Bytes += entry.Length;
                return string.Empty;
        }
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    private sealed class Walk(DateTimeOffset own)
    {
        public DateTimeOffset Newest { get; set; } = own;

        public DateTimeOffset NewestFile { get; set; } = DateTimeOffset.MinValue;

        public long Bytes { get; set; }

        public bool HoldsGit { get; set; }

        public DateTimeOffset NewestFolderCreated { get; set; } = DateTimeOffset.MinValue;
    }
}
