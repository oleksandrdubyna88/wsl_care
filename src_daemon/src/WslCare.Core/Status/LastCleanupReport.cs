using WslCare.Core.History;
using WslCare.Core.Records;

namespace WslCare.Core.Status;

/// <summary>
/// <c>status --json</c>'s <c>lastCleanup</c> (plan §7.2 <i>Last cleanup</i>, §15j M7): the newest run in the history in which
/// an action acted AND removed or freed something — the same "cleanup" <c>logs</c> counts (<see cref="RunLogs.IsCleanup"/>).
/// <c>available: false</c> with the reason when there is none yet or the history cannot be read — never a run of 0 bytes.
/// </summary>
/// <param name="FreedBytes">What its acting actions measurably freed (a failed action's real deletions included, as in <c>logs</c>).</param>
/// <param name="Count">The objects they removed.</param>
public sealed record LastCleanupReport(
    bool Available,
    string? Reason,
    string? RunId,
    DateTimeOffset? StartedAt,
    string? Trigger,
    long? FreedBytes,
    int? Count);

/// <summary>Builds <see cref="LastCleanupReport"/> from the history <c>status</c> already read — no second read, no detail file.</summary>
public static class LastCleanups
{
    public const string NoneYet = "no cleanup is recorded in the history yet (no action has removed or freed anything)";

    public static LastCleanupReport From(HistoryRead history)
    {
        if (history.Problem.Length > 0)
        {
            return Unavailable(history.Problem);
        }

        var newest = history.Records.Where(RunLogs.IsCleanup).MaxBy(r => r.StartedAt);
        if (newest is null)
        {
            return Unavailable(NoneYet);
        }

        var (count, freed) = RunLogs.Freed(newest);
        return new LastCleanupReport(true, null, newest.RunId.Text, newest.StartedAt, RunningReports.TriggerName(newest.Trigger), freed, count);
    }

    private static LastCleanupReport Unavailable(string reason) => new(false, reason, null, null, null, null, null);
}
