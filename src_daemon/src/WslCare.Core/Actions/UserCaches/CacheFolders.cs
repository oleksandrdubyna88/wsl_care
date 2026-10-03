using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Folders;
using WslCare.Core.Hosting;

namespace WslCare.Core.Actions.UserCaches;

/// <summary>One folder measured by a bounded walk: its bytes, or why not, and whether the walk reached its end.</summary>
public sealed record FolderReading(string Path, Reading<long> Bytes, bool Complete)
{
    /// <summary>The bytes when the walk was COMPLETE; a cut walk is a lower bound, never a figure to subtract.</summary>
    public long? CompleteBytes => Complete && Bytes is Reading<long>.Available { Value: var b } ? b : null;
}

/// <summary>What deleting one target folder came to: removed, or kept with why — and the failure it counts as, if any.</summary>
public sealed record FolderDeletion(bool Removed, string Note, string Failure);

/// <summary>Folder deletions sorted for an <see cref="ActionRun"/>: the items removed, those kept (each with its note), the failures.</summary>
public sealed record FolderRemovals(IReadOnlyList<ActionItem> Removed, IReadOnlyList<ActionItem> NotRemoved, IReadOnlyList<string> Failures)
{
    public static FolderRemovals Of(IReadOnlyList<(ActionItem Item, FolderDeletion Result)> results) =>
        new(
            [.. results.Where(r => r.Result.Removed).Select(r => r.Item)],
            [.. results.Where(r => !r.Result.Removed).Select(r => r.Item with { Note = r.Result.Note })],
            [.. results.Select(r => r.Result.Failure).Where(f => f.Length > 0)]);
}

/// <summary>
/// The folders the user-scoped actions (A8, A12, A14, A17) work in — always under the TARGET user's home (plan §15c #2),
/// never under <c>$HOME</c> of the root process — and the one way they measure one: a bounded walk through
/// <see cref="IFileSystem.MeasureTree"/> (links never followed, contents never read, the daily walk's ceiling).
/// </summary>
public static class CacheFolders
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The target user's home as this process sees it (under the sandbox root in a test); empty when there is
    /// no target user or this is not the distro's layout.</summary>
    public static string Home(ActionContext context) =>
        context.TargetUser is TargetUserResult.Found found && context.Paths is LinuxHostPaths linux ? linux.DistroPath(found.User.Home) : string.Empty;

    /// <summary>A folder under the target user's home; empty when <see cref="Home"/> is.</summary>
    public static string UnderHome(ActionContext context, params string[] segments)
    {
        var home = Home(context);
        return home.Length == 0 ? string.Empty : context.Paths.Rules.Join(home, segments);
    }

    /// <summary>A path as the DISTRO names it — what a process's command line holds — for one this process reads under the
    /// target user's home (the same path on the machine; under the sandbox root in a test).</summary>
    public static string ToDistro(ActionContext context, string onDisk)
    {
        var home = Home(context);
        var distroHome = context.TargetUser is TargetUserResult.Found f ? f.User.Home : string.Empty;
        return home.Length > 0 && onDisk.StartsWith(home, StringComparison.Ordinal) ? distroHome + onDisk[home.Length..].Replace('\\', '/') : onDisk;
    }

    /// <summary>The target user's name, for a sentence.</summary>
    public static string UserName(ActionContext context) => context.TargetUser is TargetUserResult.Found found ? found.User.Name : "the target user";

    public static FolderReading Measure(IFileSystem files, string path, CancellationToken cancellationToken) =>
        files.MeasureTree(path, FolderSizes.Limits, None, None, cancellationToken) switch
        {
            TreeMeasure.Measured m => new FolderReading(path, Reading.Of(m.Bytes), m.Complete),
            TreeMeasure.Missing => new FolderReading(path, Reading.Of(0L), true),
            TreeMeasure.Unreadable u => new FolderReading(path, Reading.Missing<long>($"{path} could not be read: {u.Reason}"), false),
            _ => throw new System.Diagnostics.UnreachableException("TreeMeasure is a closed set"),
        };

    /// <summary>What a cache gave back, MEASURED (plan §5): its size before minus its size after — only when both walks were
    /// complete; otherwise unknown, never an estimate.</summary>
    public static long? Freed(FolderReading before, FolderReading after) =>
        before.CompleteBytes is { } b && after.CompleteBytes is { } a ? Math.Max(0, b - a) : null;

    /// <summary>Why the user's caches cannot be previewed: the target user's refusal, or — on the Windows binary — that they
    /// are the distro's (<paramref name="what"/> names them).</summary>
    public static string NoHome(ActionContext context, string what) =>
        context.TargetUser.Refusal.Length > 0 ? context.TargetUser.Refusal : $"{what} are the WSL distro's";

    /// <summary>An item's size, 0 when unknown — for a sum that says elsewhere what it could not size.</summary>
    public static long Size(ActionItem item) => item.Bytes ?? 0;

    public static long Total(IEnumerable<ActionItem> items) => items.Sum(Size);

    /// <summary>One target folder deleted through the deletion policy (scope: <paramref name="root"/>): already gone, refused
    /// (a failure), still there after the delete, or removed.</summary>
    public static FolderDeletion RemoveFolder(ActionContext context, string folder, string root, string action) =>
        context.Files.DirectoryExists(folder)
            ? Judged(context, folder, context.Files.DeleteDirectory(folder, new DeletionScope(root, action)))
            : new FolderDeletion(false, "already gone", string.Empty);

    private static FolderDeletion Judged(ActionContext context, string folder, DeletionVerdict verdict) => verdict switch
    {
        DeletionVerdict.Refused refused => new FolderDeletion(false, refused.Reason, refused.Reason),
        _ when context.Files.DirectoryExists(folder) => new FolderDeletion(false, "still there after the delete", string.Empty),
        _ => new FolderDeletion(true, string.Empty, string.Empty),
    };

    /// <summary>A size as a person reads it in a note.</summary>
    public static string Gb(long? bytes) =>
        bytes is { } b ? string.Create(CultureInfo.InvariantCulture, $"{b / 1e9:0.00} GB") : "size unknown";
}
