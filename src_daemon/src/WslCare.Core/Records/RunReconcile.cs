using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;

namespace WslCare.Core.Records;

/// <summary>What the startup reconcile found and did (plan §15b #1).</summary>
/// <param name="Interrupted">Runs whose detail had no history line: each got one, outcome <c>interrupted</c>.</param>
/// <param name="DetailLost">Runs whose history line names a detail that is not there — shown as <i>detail lost</i>, never rewritten.</param>
public sealed record ReconcileReport(IReadOnlyList<string> Interrupted, IReadOnlyList<string> DetailLost);

/// <summary>
/// The startup reconcile of the run records (plan §15b #1, §15a #0). The write order of a run is detail → history
/// line → run log, so a run that died between the first two leaves a detail no line names; it gets a line with
/// outcome <c>interrupted</c>, and history never shows a run that silently vanished. A line whose detail is
/// missing is reported as <i>detail lost</i> and left as it is.
/// </summary>
/// <remarks>A detail older than the retention window (<see cref="RunRetention.IsAged"/>) is never an orphan: retention
/// ages a line and its detail by the same UTC day, so such a detail is one whose line was pruned. Run under the run lock (<c>collect</c> takes it first), so no detail it looks at belongs to a run still
/// writing its line.</remarks>
public static class RunReconcile
{
    public const string InterruptedReason = "the run wrote its detail and ended before its history line (found by the next run's reconcile)";
    public const string UnreadableDetailReason = "the run left a detail that cannot be read; its start is the second its id names";

    public static ReconcileReport Apply(IHostPaths paths, IFileSystem files, DateTimeOffset now)
    {
        var history = RunHistory.Read(paths, files).Records;
        var named = history.Select(r => r.DetailPath).Where(p => p.Length > 0).ToHashSet(StringComparer.Ordinal);
        var recordedIds = history.Select(r => r.RunId.Text).ToHashSet(StringComparer.Ordinal);
        var stored = RunDetailStore.List(paths, files);
        // A detail past the retention window is retention's to remove, never a run that died: its line was pruned, and
        // writing an "interrupted" line for it would resurrect the run (gate finding #0/#4).
        var orphans = stored.Where(d => !named.Contains(d.RelativePath) && !recordedIds.Contains(d.RunId.Text) && !RunRetention.IsAged(RunDetailStore.DayOf(d.RunId), now)).ToList();
        var writer = new RunRecordWriter(paths, files);
        foreach (var orphan in orphans)
        {
            writer.Append(InterruptedLine(orphan, RunDetailStore.ReadHead(paths, files, orphan.RelativePath)));
        }

        var present = stored.Select(d => d.RelativePath).ToHashSet(StringComparer.Ordinal);
        var lost = history.Where(r => r.DetailPath.Length > 0 && !present.Contains(r.DetailPath)).Select(r => r.RunId.Text).ToList();
        return new ReconcileReport([.. orphans.Select(o => o.RunId.Text)], lost);
    }

    private static RunRecord InterruptedLine(StoredDetail orphan, RunDetailHead? head)
    {
        var started = head?.StartedAt ?? new DateTimeOffset(RunDetailStore.DayOf(orphan.RunId).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return new RunRecord(Core.SchemaVersion.Current, orphan.RunId, head?.Trigger ?? RunTrigger.Timer, started, head?.EndedAt ?? started, RunOutcome.Interrupted, [])
        {
            Detail = orphan.RelativePath,
            DryRun = head?.DryRun,
            Reason = head is null ? UnreadableDetailReason : InterruptedReason,
        };
    }
}
