using System.Text.Json;

using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;
using WslCare.Core.Json;

namespace WslCare.Core.Actions.WindowsTemp;

/// <summary>
/// The candidate a run was deleting inside when it last wrote (PLAN_windows_runner.md §3.12, "Recovery after a kill mid-delete"):
/// written, atomically, BEFORE the first delete inside it. Deleting children rewrites a folder's own write time, so a resumed
/// candidate is never judged by folder times — only by what the checkpoint recorded.
/// </summary>
/// <param name="Name">The top-level entry's name; empty when no candidate is in progress.</param>
/// <param name="NewestFileWrite">The newest write time of any FILE inside it when it was judged.</param>
/// <param name="CheckpointAt">When the checkpoint was written: a folder CREATED after it did not exist when the candidate was judged.</param>
/// <param name="Owner">The owner SID seen when it was judged.</param>
public sealed record TempCheckpoint(string Name, DateTimeOffset NewestFileWrite, DateTimeOffset CheckpointAt, string Owner)
{
    public static readonly TempCheckpoint None = new(string.Empty, DateTimeOffset.MinValue, DateTimeOffset.MinValue, string.Empty);

    public bool IsNone => Name.Length == 0;
}

/// <summary>
/// <c>{state}/temp-cursor.json</c>: where the next run of A23 starts, and the candidate an interrupted run left half deleted.
/// </summary>
/// <param name="SchemaVersion">The record's version.</param>
/// <param name="Last">The name of the last candidate a run DECIDED — deleted, retained, excluded, unknown: whatever it was, the
/// cursor moves past it, so no run starts at the same place forever. Empty before the first run.</param>
/// <param name="PassStarted">When the current pass over the folder's names began.</param>
/// <param name="PassDecided">How many names the current pass has decided.</param>
/// <param name="PassesCompleted">How many passes have wrapped round to where they began.</param>
/// <param name="InProgress">The half-deleted candidate, or <see cref="TempCheckpoint.None"/>.</param>
public sealed record TempCursor(int SchemaVersion, string Last, DateTimeOffset PassStarted, int PassDecided, int PassesCompleted, TempCheckpoint InProgress)
{
    /// <summary>No run yet: the first one starts at the first name.</summary>
    public static readonly TempCursor Fresh = new(Core.SchemaVersion.Current, string.Empty, DateTimeOffset.MinValue, 0, 0, TempCheckpoint.None);
}

/// <summary>Reads and writes the cursor file; both answer a closed result, never an exception.</summary>
public static class TempCursors
{
    public const string FileName = "temp-cursor.json";

    public static string File(IHostPaths paths) => paths.Rules.Join(paths.StateDirectory, FileName);

    /// <summary>The cursor on disk; <see cref="TempCursor.Fresh"/> when there is none or it cannot be read (a pass then starts over,
    /// which only re-judges names — nothing is deleted on a cursor's say-so).</summary>
    public static TempCursor Read(IHostPaths paths, IFileSystem files) =>
        files.ReadFile(File(paths), RootFileCaps.State) is FileReadResult.Content content && Parse(content.Bytes) is { } cursor ? cursor : TempCursor.Fresh;

    /// <summary>Empty when written; otherwise why not.</summary>
    public static string Write(IHostPaths paths, IFileSystem files, TempCursor cursor)
    {
        try
        {
            files.CreateDirectory(paths.StateDirectory);
            var json = JsonSerializer.SerializeToUtf8Bytes(cursor, WslCareJsonContext.Default.TempCursor);
            return files.WriteFileAtomically(File(paths), json, new DeletionScope(paths.StateDirectory, WindowsTempAction.IdText)) is DeletionVerdict.Refused refused ? refused.Reason : string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    private static TempCursor? Parse(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize(bytes, WslCareJsonContext.Default.TempCursor) is { InProgress: not null, Last: not null } cursor ? cursor : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
