using WslCare.Core.Files;
using WslCare.Core.Records;

namespace WslCare.Core.Actions.WindowsTemp;

/// <summary>One decided candidate of a plan.</summary>
internal sealed record PlannedEntry(Candidate Candidate, Judgement Judgement, bool Resumes);

/// <summary>What planning produced: the entries decided in this run, in the order they were decided, and where the cursor goes next.</summary>
/// <param name="Root">The resolved temp folder.</param>
/// <param name="Entries">Every candidate this run decided.</param>
/// <param name="Total">All top-level names.</param>
/// <param name="Deferred">Names the budget (or the per-run maximum) stopped the run before.</param>
/// <param name="Next">The cursor as it stands once this run's plan is carried out — its <c>InProgress</c> is the one on disk until the run resolves it.</param>
/// <param name="ResolvesInProgress">The interrupted candidate was decided (finished, retained or gone): the run clears its marker.</param>
/// <param name="BudgetStopped">Some of the deferred names were deferred because the scan budget was exhausted - not because <c>temp.maxItemsPerRun</c> was reached.</param>
internal sealed record TempPlan(string Root, IReadOnlyList<PlannedEntry> Entries, int Total, int Deferred, TempCursor Next, bool ResolvesInProgress, bool BudgetStopped = false)
{
    public int Count(TempVerdict verdict) => Entries.Count(e => e.Judgement.Verdict == verdict);

    public IReadOnlyList<PlannedEntry> Eligible => [.. Entries.Where(e => e.Judgement.IsEligible)];
}

/// <summary>
/// Plans one run (PLAN_windows_runner.md §3.12): lists the top-level names, takes the interrupted candidate first, then the names after
/// the cursor in ordinal order and — when it reaches the end — the names up to the cursor's own (the wrap), each judged within the run's
/// budget, until the budget or <c>temp.maxItemsPerRun</c> stops it. EVERY candidate it reaches is decided, whatever the verdict, and
/// the cursor moves to the last one, so a run that finds nothing to delete still moves on (the story's plan round, finding 3).
/// </summary>
internal static class TempPlanner
{
    /// <param name="budget">The run's ONE budget: the time the top-level listing took is already spent from it (the coai code round,
    /// finding 11).</param>
    public static TempPlan Plan(ActionContext context, TempSettings settings, string root, IReadOnlyList<FileEntry> listed, TempBudget budget, CancellationToken token)
    {
        var now = context.Clock.GetUtcNow();
        var cursor = TempCursors.Read(context.Paths, context.Files);
        var judge = new TempJudge(context.Files, context.Paths.Rules, root, context.UserSid, settings, now, scanDisallowed: context.Trigger != RunTrigger.Timer);
        var entries = listed.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        var resume = entries.FirstOrDefault(e => !cursor.InProgress.IsNone && string.Equals(e.Name, cursor.InProgress.Name, StringComparison.OrdinalIgnoreCase));
        var rest = entries.Where(e => e != resume).ToList();
        var ahead = rest.Where(e => string.CompareOrdinal(e.Name, cursor.Last) > 0).ToList();
        var wrapped = rest.Where(e => string.CompareOrdinal(e.Name, cursor.Last) <= 0).ToList();

        var run = new Run(context, settings, root, judge, budget, token);
        if (resume is not null)
        {
            run.Decide(resume, resumes: true, cursor.InProgress);
        }

        var decidedAhead = run.DecideAll(ahead, cursor.InProgress);
        var decidedWrapped = run.DecideAll(wrapped, cursor.InProgress);
        return new TempPlan(root, run.Entries, entries.Count, run.Deferred, Next(cursor, now, run, decidedAhead, decidedWrapped), ResolvesInProgress: !cursor.InProgress.IsNone && (resume is null || run.Entries.Any(e => e.Resumes)), BudgetStopped: run.BudgetStopped);
    }

    private static TempCursor Next(TempCursor cursor, DateTimeOffset now, Run run, int decidedAhead, int decidedWrapped)
    {
        var last = run.LastDecided.Length > 0 ? run.LastDecided : cursor.Last;
        var started = cursor.PassStarted == DateTimeOffset.MinValue ? now : cursor.PassStarted;
        return decidedWrapped > 0
            ? cursor with { Last = last, PassStarted = now, PassDecided = decidedWrapped, PassesCompleted = cursor.PassesCompleted + 1 }
            : cursor with { Last = last, PassStarted = started, PassDecided = cursor.PassDecided + decidedAhead };
    }

    /// <summary>One planning pass' mutable state: what it decided so far and what is left of its budgets.</summary>
    internal sealed class Run(ActionContext context, TempSettings settings, string root, TempJudge judge, TempBudget budget, CancellationToken token)
    {
        private readonly List<PlannedEntry> _entries = [];
        private int _eligible;

        public IReadOnlyList<PlannedEntry> Entries => _entries;

        public int Deferred { get; private set; }

        /// <summary>A name was deferred because the budget ran out (the per-run maximum, checked first, is not that).</summary>
        public bool BudgetStopped { get; private set; }

        public string LastDecided { get; private set; } = string.Empty;

        /// <summary>Decides the names in turn while the budgets last; the rest are deferred. Answers how many it decided.</summary>
        public int DecideAll(IReadOnlyList<FileEntry> names, TempCheckpoint none)
        {
            var decided = 0;
            for (var i = 0; i < names.Count; i++)
            {
                if (_eligible >= settings.MaxItemsPerRun || budget.Exhausted)
                {
                    // Once a cap is reached nothing after it is decided: the rest is counted, not visited (finding 8).
                    Deferred += names.Count - i;
                    BudgetStopped |= _eligible < settings.MaxItemsPerRun;
                    break;
                }

                Decide(names[i], resumes: false, none);
                decided++;
            }

            return decided;
        }

        public void Decide(FileEntry entry, bool resumes, TempCheckpoint checkpoint)
        {
            token.ThrowIfCancellationRequested();
            var candidate = new Candidate(entry.Name, context.Paths.Rules.Join(root, entry.Name), entry.Kind, entry.Length, entry.LastWriteUtc, entry.CreatedUtc);
            var judgement = judge.Judge(candidate, budget, token, resumes ? checkpoint : TempCheckpoint.None);
            _entries.Add(new PlannedEntry(candidate, judgement, resumes));
            _eligible += judgement.IsEligible ? 1 : 0;
            LastDecided = resumes ? LastDecided : entry.Name;
        }
    }
}
