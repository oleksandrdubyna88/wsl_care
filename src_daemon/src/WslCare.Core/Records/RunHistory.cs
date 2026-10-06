using System.Globalization;
using System.Text.Json;

using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;
using WslCare.Core.Json;

namespace WslCare.Core.Records;

/// <summary>What a history line's detail file is now: none named, there, or named and gone (plan §15b #1: <i>detail lost</i>).</summary>
public enum DetailState
{
    None,
    Present,
    Lost,
}

/// <summary>One parsed line of <c>history.jsonl</c> and the state of the detail it names.</summary>
public sealed record HistoryEntry(RunRecord Record, DetailState Detail);

/// <summary>Every line of <c>history.jsonl</c> that parses, oldest first — or why the file could not be read.</summary>
/// <param name="Unparseable">Lines that did not parse (a torn last line is the residual of the locked append).</param>
public sealed record HistoryRead(IReadOnlyList<RunRecord> Records, int Unparseable, string Problem);

/// <summary>
/// <c>history.jsonl</c> read back (plan §6). One parser for every reader — <see cref="LastFullRun"/>, the
/// reconcile, retention, <c>doctor</c> — so a line one reader accepts no other refuses.
/// </summary>
public static class RunHistory
{
    public static string File(IHostPaths paths) => RunRecordWriter.HistoryFileIn(paths);

    public static HistoryRead Read(IHostPaths paths, IFileSystem files) => files.ReadFile(File(paths), RootFileCaps.History) switch
    {
        FileReadResult.Content content => Parse(System.Text.Encoding.UTF8.GetString(content.Bytes)),
        FileReadResult.Missing => new HistoryRead([], 0, string.Empty),
        FileReadResult.Unreadable u => new HistoryRead([], 0, $"{File(paths)} could not be read: {u.Reason}"),
        _ => throw new System.Diagnostics.UnreachableException("FileReadResult is a closed set"),
    };

    /// <summary>Every COMPLETE line that parses, oldest first — a trailing line without its newline is a write in progress and
    /// is ignored, not counted (<see cref="LineFiles"/>, gate finding #6).</summary>
    public static HistoryRead Parse(string text)
    {
        var lines = LineFiles.CompleteLines(text);
        var records = lines.SelectMany(ParseLine).ToList();
        return new HistoryRead(records, lines.Count - records.Count, string.Empty);
    }

    /// <summary>The lines with the state of the detail each names — what <c>logs</c> / <c>runs</c> (E3) will show.</summary>
    public static IReadOnlyList<HistoryEntry> Entries(IHostPaths paths, IFileSystem files) =>
        [.. Read(paths, files).Records.Select(r => new HistoryEntry(r, StateOf(r, paths, files)))];

    public static IEnumerable<RunRecord> ParseLine(string line)
    {
        try
        {
            return JsonSerializer.Deserialize(line, WslCareJsonContext.Compact.RunRecord) is { RunId: not null } record ? [record] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The instant a line is aged by: its start; a line that does not parse is never judged (kept).</summary>
    public static DateTimeOffset? StartedAt(string line) => ParseLine(line).FirstOrDefault()?.StartedAt;

    private static DetailState StateOf(RunRecord record, IHostPaths paths, IFileSystem files) =>
        record.DetailPath.Length == 0
            ? DetailState.None
            : files.FileExists(RunDetailStore.Absolute(paths, record.DetailPath)) ? DetailState.Present : DetailState.Lost;
}

