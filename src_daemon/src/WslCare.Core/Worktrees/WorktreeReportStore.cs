using System.Text.Json;
using System.Text.RegularExpressions;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;
using WslCare.Core.Json;

namespace WslCare.Core.Worktrees;

/// <summary>
/// <c>worktrees.json</c> under the state directory (E19.S3, PLAN_disk_hygiene_windows.md §3.3 "The report"): written atomically through
/// <see cref="IFileSystem.WriteFileAtomically"/> and the deletion policy's <see cref="DeletionScope"/> for the state directory (a reader sees
/// the old report or the new, never half), read back with a REASON when it cannot be — a missing report is "no scan yet", never an empty
/// one — and the temp file a killed scan left beside it swept at the next start: that file only, by the name the atomic write gives it, and
/// only when it is stale (a scan writing right now owns a young one). A write that cannot happen is a reason, never an exception.
/// </summary>
public sealed partial class WorktreeReportStore(IHostPaths paths, IFileSystem files)
{
    public const string FileName = "worktrees.json";

    private const string Action = "worktrees";

    public string File => paths.Rules.Join(paths.StateDirectory, FileName);

    private DeletionScope Scope => new(paths.StateDirectory, Action);

    public Reading<WorktreeReport> Read() => files.ReadRegularFile(File, Tuning.Current.Int(ConfigKeys.Records.MaxStateFileBytes)) switch
    {
        FileReadResult.Content content => Parse(content.Bytes),
        FileReadResult.Missing => Reading.Missing<WorktreeReport>($"{File} does not exist"),
        FileReadResult.Unreadable unreadable => Reading.Missing<WorktreeReport>($"{File} could not be read: {unreadable.Reason}"),
        _ => throw new System.Diagnostics.UnreachableException("FileReadResult is a closed set"),
    };

    private Reading<WorktreeReport> Parse(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize(bytes, WslCareJsonContext.Default.WorktreeReport) is { } report
                ? report.SchemaVersion == Core.SchemaVersion.Current
                    ? Reading.Of(report)
                    : Reading.Missing<WorktreeReport>($"{File} has schema {report.SchemaVersion}; this build reads schema {Core.SchemaVersion.Current}")
                : Reading.Missing<WorktreeReport>($"{File} is not a worktree report: it holds nothing");
        }
        catch (JsonException e)
        {
            return Reading.Missing<WorktreeReport>($"{File} is not a worktree report: {e.Message}");
        }
    }

    /// <summary>Writes the report; empty when it was written, else why not.</summary>
    public string TryWrite(WorktreeReport report)
    {
        try
        {
            files.CreateDirectory(paths.StateDirectory);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(report, WslCareJsonContext.Default.WorktreeReport);
            return files.WriteFileAtomically(File, bytes, Scope) is DeletionVerdict.Refused refused
                ? $"the deletion policy refused the report: {refused.Reason}"
                : string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"{File} could not be written: {e.Message}";
        }
    }

    /// <summary>The temp file the atomic write names: the report's name, a dot, 32 hex digits, <c>.tmp</c>.</summary>
    [GeneratedRegex(@"^worktrees\.json\.[0-9a-f]{32}\.tmp$", RegexOptions.CultureInvariant)]
    private static partial Regex OwnTempName();

    /// <summary>Removes the temp files of a scan that was killed between writing and renaming: this report's own temp names in the state
    /// directory, last written at least <paramref name="olderThan"/> ago. Answers how many went.</summary>
    public int SweepStaleTemp(DateTimeOffset now, TimeSpan olderThan) =>
        files.ListEntries(paths.StateDirectory)
            .Where(e => e.Kind == EntryKind.File && OwnTempName().IsMatch(e.Name) && now - e.LastWriteUtc >= olderThan)
            .Count(e => files.DeleteFile(paths.Rules.Join(paths.StateDirectory, e.Name), Scope).IsAllowed);
}
