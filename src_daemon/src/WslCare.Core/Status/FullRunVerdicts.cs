using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using WslCare.Core.Collectors;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Records;
using WslCare.Core.Thresholds;

namespace WslCare.Core.Status;

/// <summary>The part of a run detail <c>status</c> reads back: its head and the thresholds the run evaluated. Every other
/// member of the detail is skipped by the reader, so its cost is one file read, not the whole detail's types.</summary>
/// <param name="Disk">E15.S2: the run's disk section, kept as raw JSON and parsed on its own — so a disk section that does not parse
/// costs only <c>disk</c>, never the verdicts carried with it (own review, 2026-10-11); absent (<c>null</c>) in a detail written before
/// E15.S2.</param>
/// <param name="Defender">E15.S10: the run's Defender section, kept as raw JSON the same way — a section that does not parse costs only
/// <c>defender</c>; absent in a detail written before E15.S10.</param>
public sealed record RunDetailVerdicts(int SchemaVersion, RunId RunId, DateTimeOffset StartedAt, DateTimeOffset EndedAt, IReadOnlyList<Verdict>? Thresholds, JsonElement? Disk, JsonElement? Defender);

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
        files.ReadFile(RunDetailStore.Absolute(paths, relative), RootFileCaps.History) switch
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
                ? Reading.Of(new RecordedVerdicts(detail.RunId, detail.EndedAt, [.. (detail.Thresholds ?? []).SelectMany(Normalised)]) { Disk = Disk(detail), Defender = Defender(detail) })
                : Reading.Missing<RecordedVerdicts>($"the detail of the newest full run ({relative}) does not parse: it names no run");
        }
        catch (JsonException e)
        {
            return Reading.Missing<RecordedVerdicts>($"the detail of the newest full run ({relative}) does not parse: {e.Message}");
        }
    }

    /// <summary>E15.S2: the detail's disk section, or why it has none (written before E15.S2).</summary>
    private static Reading<Collect.DiskReport> Disk(RunDetailVerdicts detail) =>
        Section(detail.Disk, WslCareJsonContext.Default.DiskReport, detail.RunId, "disk", "E15.S2");

    /// <summary>E15.S10: the detail's Defender section, or why it has none (written before E15.S10).</summary>
    private static Reading<Collect.DefenderReport> Defender(RunDetailVerdicts detail) =>
        Section(detail.Defender, WslCareJsonContext.Default.DefenderReport, detail.RunId, "defender", "E15.S10");

    /// <summary>A section of the detail kept as raw JSON and parsed on its own — so one that does not parse costs only that section, never the
    /// verdicts carried with it (own review, 2026-10-11) — or why there is none: the detail was written before <paramref name="since"/>.</summary>
    private static Reading<T> Section<T>(JsonElement? raw, JsonTypeInfo<T> info, RunId runId, string name, string since)
        where T : class =>
        raw is { ValueKind: not JsonValueKind.Null } section
            ? Parsed(section, info, runId, name)
            : Reading.Missing<T>($"the newest full run ({runId}) recorded no {name} section (its detail was written before {since})");

    private static Reading<T> Parsed<T>(JsonElement section, JsonTypeInfo<T> info, RunId runId, string name)
        where T : class
    {
        try
        {
            return section.Deserialize(info) is { } report
                ? Reading.Of(report)
                : Reading.Missing<T>($"the {name} section of the newest full run ({runId}) is empty");
        }
        catch (JsonException e)
        {
            return Reading.Missing<T>($"the {name} section of the newest full run ({runId}) does not parse: {e.Message}");
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
