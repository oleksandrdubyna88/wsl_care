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
/// <remarks>Run under the run lock (<c>collect</c> takes it first), so no detail it looks at belongs to a run still
/// writing its line.</remarks>
public static class RunReconcile
{
    public const string InterruptedReason = "the run wrote its detail and ended before its history line (found by the next run's reconcile)";
    public const string UnreadableDetailReason = "the run left a detail that cannot be read; its start is the second its id names";

    public static ReconcileReport Apply(IHostPaths paths, IFileSystem files)
    {
        var history = RunHistory.Read(paths, files).Records;
        var named = history.Select(r => r.DetailPath).Where(p => p.Length > 0).ToHashSet(StringComparer.Ordinal);
        var recordedIds = history.Select(r => r.RunId.Text).ToHashSet(StringComparer.Ordinal);
        var stored = RunDetailStore.List(paths, files);
        var orphans = stored.Where(d => !named.Contains(d.RelativePath) && !recordedIds.Contains(d.RunId.Text)).ToList();
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

/// <summary>What one retention sweep removed (plan §6's table), and what it could not.</summary>
public sealed record RetentionReport(int HistoryLinesRemoved, IReadOnlyList<string> DetailsRemoved, IReadOnlyList<string> Problems);

/// <summary>
/// Retention of the run records (plan §6: history and details 90 days). The history line ages first; a detail is
/// removed only once NO remaining line names it (plan §15b #1), and only in a day folder older than the window —
/// so a detail outlives its line by at most one sweep, never the other way round. Leftover temporary files of an
/// atomic write that died go too. Every delete passes <see cref="IFileSystem"/> with <c>runs/</c> as the declared
/// root; the history rewrite takes the append lock.
/// </summary>
public static class RunRetention
{
    public const int RetentionDays = 90;
    private const string Action = "run-retention";

    public static RetentionReport Sweep(IHostPaths paths, IFileSystem files, DateTimeOffset now)
    {
        var cutoff = now.AddDays(-RetentionDays);
        var problems = new List<string>();
        var removedLines = TrimHistory(paths, files, cutoff, problems);
        var named = RunHistory.Read(paths, files).Records.Select(r => r.DetailPath).Where(p => p.Length > 0).ToHashSet(StringComparer.Ordinal);
        var scope = new DeletionScope(RunDetailStore.Root(paths), Action);
        var removed = new List<string>();
        foreach (var day in files.ListDirectories(RunDetailStore.Root(paths)))
        {
            SweepDay(files, day, DateOnly.FromDateTime(cutoff.UtcDateTime), named, scope, removed, problems);
        }

        return new RetentionReport(removedLines, removed, problems);
    }

    private static int TrimHistory(IHostPaths paths, IFileSystem files, DateTimeOffset cutoff, List<string> problems)
    {
        var removed = 0;
        var verdict = files.RewriteLines(RunHistory.File(paths), lines =>
        {
            IReadOnlyList<string> kept = [.. lines.Where(l => RunHistory.StartedAt(l) is not { } started || started >= cutoff)];
            removed = lines.Count - kept.Count;
            return kept;
        }, new DeletionScope(paths.StateDirectory, Action), RunRecordWriter.LockTimeout);
        if (verdict is DeletionVerdict.Refused refused)
        {
            problems.Add(refused.Reason);
            return 0;
        }

        return removed;
    }

    private static void SweepDay(IFileSystem files, string day, DateOnly cutoffDay, IReadOnlySet<string> named, DeletionScope scope, List<string> removed, List<string> problems)
    {
        var name = Path.GetFileName(day);
        var old = DateOnly.TryParseExact(name, "yyyy-MM-dd", out var date) && date < cutoffDay;
        foreach (var file in files.ListFiles(day))
        {
            var relative = $"{RunDetailStore.Folder}/{name}/{Path.GetFileName(file)}";
            var leftover = file.EndsWith(".tmp", StringComparison.Ordinal);
            if (leftover || (old && !named.Contains(relative)))
            {
                Remove(() => files.DeleteFile(file, scope), relative, removed, problems);
            }
        }

        if (old && files.ListFiles(day).Count == 0 && files.ListDirectories(day).Count == 0)
        {
            Remove(() => files.DeleteDirectory(day, scope), $"{RunDetailStore.Folder}/{name}/", removed, problems);
        }
    }

    private static void Remove(Func<DeletionVerdict> delete, string what, List<string> removed, List<string> problems)
    {
        try
        {
            if (delete() is DeletionVerdict.Refused refused)
            {
                problems.Add(refused.Reason);
                return;
            }

            removed.Add(what);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{what}: {e.Message}");
        }
    }
}
