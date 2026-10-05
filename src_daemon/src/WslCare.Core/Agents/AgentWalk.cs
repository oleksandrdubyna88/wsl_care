using WslCare.Core.Files;
using WslCare.Core.Folders;

namespace WslCare.Core.Agents;

/// <summary>One agent to measure: its catalogue entry, its data folders as paths on this side, and where its session layout
/// starts (empty = sessions not counted here).</summary>
public sealed record AgentTarget(AgentEntry Entry, IReadOnlyList<string> Folders, string SessionsUnder);

/// <summary>
/// Measures the AI agents' data folders (plan §4.6, §15q D1, R2.3) — sizes and counts, nothing read, moved or deleted
/// (H1–H3): a bounded walk per folder that never follows a link, never enters <see cref="AgentCatalogue.Memory"/> (of ANY
/// agent, H2) nor the entry's own never-enter names and prefixes, and stops at a change of filesystem (review C1); and the
/// sessions of a confirmed layout by listing names (<see cref="SessionGlob"/>).
/// </summary>
/// <remarks>ONE total budget for the whole walk (review M7): catalogue order, each folder under the per-folder ceiling of the
/// daily walk or what is left of the budget, whichever is less; what the budget does not reach is "not measured this run" —
/// a reason, never 0.</remarks>
public sealed class AgentWalk(IFileSystem files, TimeProvider clock)
{
    /// <summary>The whole agent walk inside a root <c>collect</c> (review M7).</summary>
    public static readonly TimeSpan CollectBudget = TimeSpan.FromMinutes(3);

    /// <summary><c>agents list --measure</c>: below every host call's timeout (E7.S4 states the client's).</summary>
    public static readonly TimeSpan MeasureNowBudget = TimeSpan.FromSeconds(60);

    public const string NotReached = "not measured this run: the agent walk's time budget was spent before this folder";

    /// <summary>Measures <paramref name="targets"/> within <paramref name="budget"/>; <paramref name="withNames"/> keeps the five
    /// largest sessions by name (a live answer only — never what a history line carries).</summary>
    public AgentsSample Measure(IReadOnlyList<AgentTarget> targets, TimeSpan budget, bool withNames, CancellationToken cancellationToken)
    {
        var started = clock.GetTimestamp();
        bool OutOfTime() => clock.GetElapsedTime(started) >= budget;
        TimeSpan Left() => budget - clock.GetElapsedTime(started);
        var agents = targets.Select(t => MeasureOne(t, OutOfTime, Left, withNames, cancellationToken)).ToList();
        return new AgentsSample(clock.GetUtcNow(), agents);
    }

    /// <summary>What a walk of this entry declines to enter.</summary>
    public static TreeRules RulesFor(AgentEntry entry) =>
        new(new HashSet<string>(StringComparer.Ordinal), NeverEnterOf(entry)) { NeverEnterPrefixes = entry.NeverEnterPrefixes, StayOnDevice = true };

    /// <summary><see cref="AgentCatalogue.Memory"/> and the entry's own names.</summary>
    public static IReadOnlySet<string> NeverEnterOf(AgentEntry entry) =>
        new HashSet<string>([AgentCatalogue.Memory, .. entry.NeverEnter], StringComparer.Ordinal);

    private AgentSize MeasureOne(AgentTarget target, Func<bool> outOfTime, Func<TimeSpan> left, bool withNames, CancellationToken cancellationToken)
    {
        var folders = target.Folders.Select(f => MeasureFolder(f, RulesFor(target.Entry), left, cancellationToken)).ToList();
        var (sessions, largest) = CountSessions(target, outOfTime);
        return new AgentSize(target.Entry.Id, folders, sessions) { Largest = withNames ? largest : null };
    }

    private AgentFolderSize MeasureFolder(string path, TreeRules rules, Func<TimeSpan> left, CancellationToken cancellationToken)
    {
        if (!files.DirectoryExists(path))
        {
            return new AgentFolderSize(path, false, 0, 0, true, [], $"{path} does not exist");
        }

        // The time left is read ONCE: a walk is never started with a ceiling the budget no longer holds.
        var remaining = left();
        return remaining <= TimeSpan.Zero
            ? new AgentFolderSize(path, true, 0, 0, false, [], NotReached)
            : Sized(path, files.WalkTree(path, new TreeLimits(FolderSizes.Limits.MaxEntries, Shorter(FolderSizes.Limits.MaxDuration, remaining)), rules, cancellationToken));
    }

    private static TimeSpan Shorter(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static AgentFolderSize Sized(string path, TreeMeasure measure) => measure switch
    {
        TreeMeasure.Measured m => new AgentFolderSize(path, true, m.Bytes, m.Files, m.Complete, m.Excluded, m.Note),
        TreeMeasure.Missing => new AgentFolderSize(path, false, 0, 0, true, [], $"{path} does not exist"),
        TreeMeasure.Unreadable u => new AgentFolderSize(path, true, 0, 0, false, [], u.Reason),
        _ => throw new System.Diagnostics.UnreachableException("TreeMeasure is a closed set"),
    };

    private (SessionFigures Figures, IReadOnlyList<SessionName> Largest) CountSessions(AgentTarget target, Func<bool> outOfTime)
    {
        if (target.Entry.Sessions is not { } layout || target.SessionsUnder.Length == 0)
        {
            return (SessionFigures.NotCounted("monitor only: this agent's session layout is not confirmed on this side"), []);
        }

        if (!files.DirectoryExists(target.SessionsUnder))
        {
            return (SessionFigures.NotCounted($"{target.SessionsUnder} does not exist"), []);
        }

        return outOfTime()
            ? (SessionFigures.NotCounted(NotReached), [])
            : Figures(SessionGlob.Find(files, target.SessionsUnder, layout.Glob, NeverEnterOf(target.Entry), outOfTime));
    }

    private static (SessionFigures, IReadOnlyList<SessionName>) Figures(SessionScan scan)
    {
        var sessions = scan.Sessions;
        var largest = sessions.Select(s => s.Session).OrderByDescending(s => s.Bytes).ThenBy(s => s.Name, StringComparer.Ordinal).Take(5).ToList();
        return (new SessionFigures(
                true,
                sessions.Count,
                sessions.Count == 0 ? null : sessions.Min(s => s.LastWrite),
                sessions.Count == 0 ? null : sessions.Max(s => s.LastWrite),
                largest.Count == 0 ? 0 : largest[0].Bytes,
                scan.Complete,
                scan.Note),
            largest);
    }
}
