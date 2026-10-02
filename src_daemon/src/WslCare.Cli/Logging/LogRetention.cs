using System.Globalization;

using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;

namespace WslCare.Cli.Logging;

/// <summary>What one startup prune did: the day folders it removed, the ones the policy refused, the ones that failed.</summary>
internal sealed record PruneReport(IReadOnlyList<string> Deleted, IReadOnlyList<string> Refused, IReadOnlyList<string> Failed);

/// <summary>
/// The named owner of the log directory's retention (family logging rule): a startup prune of
/// whole DAY FOLDERS older than the configured window — never individual files, so the folder name
/// is the age and today's folder is structurally never eligible.
/// </summary>
/// <remarks>
/// Every delete goes through <see cref="IFileSystem"/> with the log root as the declared scope, so
/// the <see cref="DeletionPolicy"/> judges it like any cleanup: a day folder that is a link into a
/// protected place is refused, not followed. Housekeeping never stops a start — a failure is
/// counted and reported, never thrown.
/// </remarks>
internal static class LogRetention
{
    public const string ActionName = "log-retention";

    /// <summary>The day folders a prune at <paramref name="todayUtc"/> deletes. Pure — the decision, apart from the deleting.</summary>
    public static IReadOnlyList<string> FoldersToPrune(IEnumerable<string> dayFolderNames, DateOnly todayUtc, int retainDays)
    {
        if (retainDays <= 0)
        {
            return []; // 0 disables the sweep.
        }

        var cutoff = todayUtc.AddDays(-retainDays);
        return [.. dayFolderNames.Where(name =>
            DateOnly.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day < cutoff)];
    }

    public static PruneReport Prune(IFileSystem files, string logRoot, DateOnly todayUtc, int retainDays)
    {
        var scope = new DeletionScope(logRoot, ActionName);
        var folders = files.ListDirectories(logRoot);
        var names = folders.Select(f => Path.GetFileName(f) ?? string.Empty).ToList();
        var deleted = new List<string>();
        var refused = new List<string>();
        var failed = new List<string>();
        foreach (var name in FoldersToPrune(names, todayUtc, retainDays))
        {
            PruneOne(files, Path.Combine(logRoot, name), scope, deleted, refused, failed);
        }

        return new PruneReport(deleted, refused, failed);
    }

    private static void PruneOne(IFileSystem files, string folder, DeletionScope scope, List<string> deleted, List<string> refused, List<string> failed)
    {
        try
        {
            switch (files.DeleteDirectory(folder, scope))
            {
                case DeletionVerdict.Refused r:
                    refused.Add($"{r.Rule}: {r.Reason}");
                    break;
                default:
                    deleted.Add(folder);
                    break;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            failed.Add($"{folder}: {e.Message}");
        }
    }
}
