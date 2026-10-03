using System.Text.Json;

using WslCare.Core.Collectors;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Records;
using WslCare.Core.Thresholds;

namespace WslCare.Core.Status;

/// <summary>The part of a run detail <c>status</c> reads back: its head and the thresholds the run evaluated. Every other
/// member of the detail is skipped by the reader, so its cost is one file read, not the whole detail's types.</summary>
public sealed record RunDetailVerdicts(int SchemaVersion, RunId RunId, DateTimeOffset StartedAt, DateTimeOffset EndedAt, IReadOnlyList<Verdict>? Thresholds);

/// <summary>
/// The verdicts of the newest FULL run, read back from its detail (plan §15g B1). A full run is a <c>collect</c> line — the
/// only line that carries the slow parts — that names its detail file; <c>act</c> lines and the <c>interrupted</c> lines of
/// the reconcile are not. One file is read; nothing is written.
/// </summary>
public static class FullRunVerdicts
{
    public static Reading<RecordedVerdicts> Read(IHostPaths paths, IFileSystem files, HistoryRead history)
    {
        if (history.Problem.Length > 0)
        {
            return Reading.Missing<RecordedVerdicts>(history.Problem);
        }

        var newest = history.Records.LastOrDefault(r => IsFullRun(r) && r.DetailPath.Length > 0);
        return newest is null ? NoDetail(history) : FromDetail(paths, files, newest.DetailPath);
    }

    private static bool IsFullRun(RunRecord record) => record.Slow is not null;

    private static Reading<RecordedVerdicts> NoDetail(HistoryRead history) =>
        history.Records.LastOrDefault(IsFullRun) is { } newest
            ? Reading.Missing<RecordedVerdicts>($"no recorded full run names its detail file (the newest, {newest.RunId}, wrote none: {newest.Reason ?? "no reason recorded"})")
            : Reading.Missing<RecordedVerdicts>(LastFullRun.NoFullRunYet);

    private static Reading<RecordedVerdicts> FromDetail(IHostPaths paths, IFileSystem files, string relative) =>
        files.ReadFile(RunDetailStore.Absolute(paths, relative)) switch
        {
            FileReadResult.Content content => Parse(content.Bytes, relative),
            FileReadResult.Unreadable u => Reading.Missing<RecordedVerdicts>($"the detail of the newest full run ({relative}) could not be read: {u.Reason}"),
            _ => Reading.Missing<RecordedVerdicts>($"the detail of the newest full run ({relative}) could not be read: it is gone"),
        };

    private static Reading<RecordedVerdicts> Parse(byte[] json, string relative)
    {
        try
        {
            return JsonSerializer.Deserialize(json, WslCareJsonContext.Default.RunDetailVerdicts) is { RunId: not null } detail
                ? Reading.Of(new RecordedVerdicts(detail.RunId, detail.EndedAt, [.. (detail.Thresholds ?? []).SelectMany(Normalised)]))
                : Reading.Missing<RecordedVerdicts>($"the detail of the newest full run ({relative}) does not parse: it names no run");
        }
        catch (JsonException e)
        {
            return Reading.Missing<RecordedVerdicts>($"the detail of the newest full run ({relative}) does not parse: {e.Message}");
        }
    }

    /// <summary>A stored verdict as a record whose strings are never null (C# doctrine §4a: the deserializer runs no
    /// defaults); one without an id is no verdict.</summary>
    private static IEnumerable<Verdict> Normalised(Verdict? stored) =>
        stored is { Id.Length: > 0 }
            ? [stored with { Value = Text(stored.Value), Limit = Text(stored.Limit), Reason = Text(stored.Reason) }]
            : [];

    private static string Text(string? stored) => stored ?? string.Empty;
}
