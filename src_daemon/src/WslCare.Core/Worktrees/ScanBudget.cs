using System.Diagnostics;

using WslCare.Core.Files;

namespace WslCare.Core.Worktrees;

/// <summary>
/// The budget of a scan (<c>worktrees.scanMaxEntries</c>, <c>worktrees.scanMaxSeconds</c>): entries still to visit and the instant it ends,
/// shared fairly — each root draws on an equal share of what is left, and gives back what it did not use — so one huge root cannot leave
/// the others unread. A walk that runs out of it marks the budget <see cref="RanOut"/>: the folders it left are unknown, never idle.
/// </summary>
internal sealed class ScanBudget
{
    private readonly Stopwatch _watch;
    private readonly TimeSpan _deadline;
    private long _entriesLeft;
    private long _spent;

    public ScanBudget(long entries, TimeSpan time, Stopwatch watch)
        : this(watch, entries, watch.Elapsed + time)
    {
    }

    private ScanBudget(Stopwatch watch, long entries, TimeSpan deadline)
    {
        _watch = watch;
        _entriesLeft = entries;
        _deadline = deadline;
    }

    /// <summary>A walk stopped at this budget's end: whatever it was walking is a lower bound.</summary>
    public bool RanOut { get; private set; }

    public bool OutOfTime => _watch.Elapsed > _deadline;

    /// <summary>The limits the next walk runs under: the entries and the time still left.</summary>
    public TreeLimits Limits() => new((int)Math.Clamp(_entriesLeft, 0, int.MaxValue), _deadline > _watch.Elapsed ? _deadline - _watch.Elapsed : TimeSpan.Zero);

    /// <summary>What a walk visited — its files AND the folders it entered (coai code round: a tree of empty folders costs entries too) — and
    /// whether it stopped short.</summary>
    public void Spend(TreeMeasure.Measured walked)
    {
        Charge(walked.Files + walked.Folders);
        RanOut |= !walked.Complete;
    }

    /// <summary>The entries a root's listing returned: they are visited too, so they come out of the same budget.</summary>
    public void SpendListing(int entries)
    {
        Charge(entries);
        RanOut |= _entriesLeft < 0;
    }

    private void Charge(long entries)
    {
        _entriesLeft -= entries;
        _spent += entries;
    }

    public void MarkRanOut() => RanOut = true;

    /// <summary>An equal share of what is left for one of <paramref name="rootsLeft"/> roots still to scan.</summary>
    public ScanBudget Share(int rootsLeft) =>
        new(_watch, _entriesLeft / Math.Max(rootsLeft, 1), _watch.Elapsed + ((_deadline > _watch.Elapsed ? _deadline - _watch.Elapsed : TimeSpan.Zero) / Math.Max(rootsLeft, 1)));

    /// <summary>The share given back: what it spent is spent, and if it ran out the whole scan is incomplete.</summary>
    public void Settle(ScanBudget share)
    {
        _entriesLeft -= share._spent;
        RanOut |= share.RanOut;
    }
}
