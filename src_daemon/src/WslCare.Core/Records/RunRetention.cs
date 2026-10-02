using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;

namespace WslCare.Core.Records;

/// <summary>What one retention sweep removed (plan §6's table), and what it could not.</summary>
public sealed record RetentionReport(int HistoryLinesRemoved, IReadOnlyList<string> DetailsRemoved, IReadOnlyList<string> Problems);

/// <summary>
/// Retention of the run records (plan §6: history and details 90 days), by ONE boundary: the UTC DAY. A history line
/// is aged when the UTC day of its start is before <see cref="CutoffDay"/>; a <c>runs/{yyyy-MM-dd}/</c> folder is aged
/// when its day is — so a line and the detail it names (filed under the same day, the day its run id carries) age out
/// in the same sweep, and the reconcile, which ignores aged details (<see cref="IsAged"/>), can never resurrect a pruned
/// run as <c>interrupted</c> (gate finding #0/#4). A detail is removed only once NO remaining line names it (plan §15b
/// #1); an aged day folder no line names is removed WHOLE, strays included (gate finding #11); leftover temporary files
/// of an atomic write that died go from every day. Every delete passes <see cref="IFileSystem"/> with <c>runs/</c> as
/// the declared root; the history is read, filtered and rewritten under ONE acquisition of its append lock
/// (<see cref="IFileSystem.RewriteLines"/>), so an append that arrives meanwhile waits and lands in the new file.
/// </summary>
public static class RunRetention
{
    public const int RetentionDays = 90;
    private const string Action = "run-retention";

    /// <summary>The first UTC day still inside the window at <paramref name="now"/>.</summary>
    public static DateOnly CutoffDay(DateTimeOffset now) => DateOnly.FromDateTime(now.UtcDateTime).AddDays(-RetentionDays);

    /// <summary>Whether a record of <paramref name="day"/> is past the window — the one rule retention and the reconcile share.</summary>
    public static bool IsAged(DateOnly day, DateTimeOffset now) => day < CutoffDay(now);

    public static RetentionReport Sweep(IHostPaths paths, IFileSystem files, DateTimeOffset now)
    {
        var problems = new List<string>();
        var removedLines = TrimHistory(paths, files, now, problems);
        var named = RunHistory.Read(paths, files).Records.Select(r => r.DetailPath).Where(p => p.Length > 0).ToHashSet(StringComparer.Ordinal);
        var scope = new DeletionScope(RunDetailStore.Root(paths), Action);
        var removed = new List<string>();
        foreach (var day in files.ListDirectories(RunDetailStore.Root(paths)))
        {
            new DaySweep(files, day, named, scope, removed, problems).Run(now);
        }

        return new RetentionReport(removedLines, removed, problems);
    }

    private static int TrimHistory(IHostPaths paths, IFileSystem files, DateTimeOffset now, List<string> problems)
    {
        var removed = 0;
        var verdict = files.RewriteLines(RunHistory.File(paths), lines =>
        {
            IReadOnlyList<string> kept = [.. lines.Where(l => RunHistory.StartedAt(l) is not { } started || !IsAged(DateOnly.FromDateTime(started.UtcDateTime), now))];
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

    /// <summary>One day folder's sweep: what it removes goes to <paramref name="removed"/>, what it could not to <paramref name="problems"/>.</summary>
    private sealed class DaySweep(IFileSystem files, string day, IReadOnlySet<string> named, DeletionScope scope, List<string> removed, List<string> problems)
    {
        private readonly string _name = Path.GetFileName(day);

        /// <summary>An aged day no line names goes whole; any other day keeps what a line names and what is inside the window.</summary>
        public void Run(DateTimeOffset now)
        {
            var aged = DateOnly.TryParseExact(_name, "yyyy-MM-dd", out var date) && IsAged(date, now);
            if (aged && !named.Any(n => n.StartsWith($"{RunDetailStore.Folder}/{_name}/", StringComparison.Ordinal)))
            {
                RemoveWhole();
                return;
            }

            RemoveFiles(aged);
        }

        /// <summary>An aged day no line names: the whole folder, strays included; its details are reported by name.</summary>
        private void RemoveWhole()
        {
            var details = files.ListFiles(day).Where(f => !IsLeftover(f)).Select(Relative).ToList();
            if (Remove(() => files.DeleteDirectory(day, scope), $"{RunDetailStore.Folder}/{_name}/"))
            {
                removed.AddRange(details);
            }
        }

        /// <summary>A day a line still names, or one inside the window: leftover temporary files, and — when aged — every
        /// detail no line names; the folder itself once that left it empty.</summary>
        private void RemoveFiles(bool aged)
        {
            foreach (var file in files.ListFiles(day).Where(f => IsLeftover(f) || (aged && !named.Contains(Relative(f)))))
            {
                Remove(() => files.DeleteFile(file, scope), Relative(file));
            }

            if (aged && files.ListFiles(day).Count == 0 && files.ListDirectories(day).Count == 0)
            {
                Remove(() => files.DeleteDirectory(day, scope), $"{RunDetailStore.Folder}/{_name}/");
            }
        }

        private string Relative(string file) => $"{RunDetailStore.Folder}/{_name}/{Path.GetFileName(file)}";

        private static bool IsLeftover(string file) => file.EndsWith(".tmp", StringComparison.Ordinal);

        private bool Remove(Func<DeletionVerdict> delete, string what)
        {
            try
            {
                if (delete() is DeletionVerdict.Refused refused)
                {
                    problems.Add(refused.Reason);
                    return false;
                }

                removed.Add(what);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{what}: {e.Message}");
                return false;
            }
        }
    }
}
