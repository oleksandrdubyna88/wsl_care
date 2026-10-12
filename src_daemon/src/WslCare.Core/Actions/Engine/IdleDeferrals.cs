using WslCare.Core.Records;

namespace WslCare.Core.Actions.Engine;

/// <summary>
/// How long the timer has deferred an action for idle (2026-10-12, <c>archive.maxDeferHours</c>): the streak of its LIVE timer
/// deferrals in <c>history.jsonl</c>.
/// </summary>
/// <remarks>
/// The lines are taken newest first by their start, and a line that does not name the action is ignored. A line continues the
/// streak only when it is a live timer run (<c>trigger</c> timer, <c>dryRun</c> false) whose status for the action is
/// <c>deferred</c>; any other line naming the action ends the walk — <c>ran</c> (a button too), <c>failed</c>, <c>refused</c>,
/// <c>interrupted</c>, <c>skipped</c>, <c>dryRun</c>, a dry-run timer run (the idle gate is asked before the dry-run gate, so a
/// busy dry-run week writes <c>deferred</c> lines that are no wait the live timer did: the coai consultant's case), and a line
/// written before <c>status</c> or <c>dryRun</c> existed, which is never evidence.
/// </remarks>
public static class IdleDeferrals
{
    /// <summary>When the action's current streak of live timer deferrals began; <c>null</c> when there is none.</summary>
    public static DateTimeOffset? StreakStart(IReadOnlyList<RunRecord> records, string actionId)
    {
        var naming = records
            .Select(r => (Record: r, Action: r.Actions.FirstOrDefault(a => a.Id == actionId)))
            .Where(p => p.Action is not null)
            .OrderByDescending(p => p.Record.StartedAt);
        return naming.TakeWhile(p => IsLiveTimerDeferral(p.Record, p.Action!)).Select(p => (DateTimeOffset?)p.Record.StartedAt).LastOrDefault();
    }

    private static bool IsLiveTimerDeferral(RunRecord record, ActionRecord action) =>
        record.Trigger == RunTrigger.Timer && record.DryRun == false && action.Status == ActionStatus.Deferred;
}
