using System.Text.Json;

using WslCare.Core.Collectors;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Json;

namespace WslCare.Core.Records;

/// <summary>One container's line of <c>docker stats --no-stream</c> (plan §4.2), as a full run stores it.</summary>
public sealed record ContainerStat(string Id, string Name, long MemoryBytes, double CpuPercent);

/// <summary><c>docker stats</c> as one full run sampled it — or why it could not.</summary>
/// <param name="Unavailable">Empty when sampled; otherwise the reason (daemon stopped, socket refused,
/// timeout — plan §15b #7).</param>
public sealed record ContainerStatsSample(DateTimeOffset SampledAt, IReadOnlyList<ContainerStat> Containers, string Unavailable);

/// <summary>The distro clock against Windows' (plan §4.5, §15b #5): the measured offset with the
/// launch latency of the probe already subtracted, and that latency.</summary>
public sealed record WindowsClockSample(DateTimeOffset SampledAt, double OffsetSeconds, double LaunchLatencySeconds, string Unavailable);

/// <summary>
/// The parts of a sample that need a SLOW process — <c>docker stats</c> and <c>powershell.exe
/// Get-Date</c> — and so are taken only by <c>collect</c> (plan §15b #5). A full run's
/// <c>history.jsonl</c> line carries them; <c>status</c> reads them back with their age.
/// </summary>
/// <remarks>E2.S1 fixes the shape and the reader; <c>collect</c> (E2.S3) and the Docker collector
/// (E2.S2) are the writers. Either member is absent from a run that did not sample it, and a missing
/// member reads as <c>null</c> under the source generator whatever the declaration says (C# doctrine
/// §4a), so every read goes through <see cref="LastFullRun"/>, which treats both the same.</remarks>
public sealed record SlowParts
{
    public ContainerStatsSample? ContainerStats { get; init; }

    public WindowsClockSample? WindowsClock { get; init; }
}

/// <summary>A slow part as <c>status</c> reports it: the value, the run it came from, when it was
/// sampled and how old it is — or why there is none.</summary>
public sealed record AgedPart<T>(T Value, RunId RunId, DateTimeOffset SampledAt, TimeSpan Age);

/// <summary>The slow parts of the newest full run that sampled each one.</summary>
public sealed record LastSlowParts(Reading<AgedPart<ContainerStatsSample>> ContainerStats, Reading<AgedPart<WindowsClockSample>> WindowsClock);

/// <summary>
/// Reads the slow parts back from <c>history.jsonl</c>: for each part, the newest line that carries it.
/// A line that does not parse is skipped (plan §6: a torn last line is the residual of the locked
/// append); a missing history is the ordinary state before the first full run.
/// </summary>
public static class LastFullRun
{
    public const string NoFullRunYet = "no full run has been recorded yet; \"wsl-care collect\" (run as root, or by the timer) records one";

    public static LastSlowParts Read(IHostPaths paths, IFileSystem files, TimeProvider clock)
    {
        var history = new RunRecordWriter(paths, files).HistoryFile;
        var now = clock.GetUtcNow();
        return files.ReadFile(history) switch
        {
            FileReadResult.Content content => FromRecords(NewestFirst(content.Bytes), now),
            FileReadResult.Missing => new LastSlowParts(Reading.Missing<AgedPart<ContainerStatsSample>>(NoFullRunYet), Reading.Missing<AgedPart<WindowsClockSample>>(NoFullRunYet)),
            FileReadResult.Unreadable u => Unreadable($"{history} could not be read: {u.Reason}"),
            _ => throw new System.Diagnostics.UnreachableException("FileReadResult is a closed set"),
        };
    }

    private static LastSlowParts FromRecords(IReadOnlyList<RunRecord> newestFirst, DateTimeOffset now) =>
        new(
            Newest(newestFirst, r => r.Slow?.ContainerStats, s => s.SampledAt, s => s.Unavailable, now, "docker stats"),
            Newest(newestFirst, r => r.Slow?.WindowsClock, s => s.SampledAt, s => s.Unavailable, now, "the Windows clock"));

    private static Reading<AgedPart<T>> Newest<T>(IReadOnlyList<RunRecord> newestFirst, Func<RunRecord, T?> part, Func<T, DateTimeOffset> sampledAt, Func<T, string?> unavailable, DateTimeOffset now, string what)
        where T : class
    {
        var record = newestFirst.FirstOrDefault(r => part(r) is not null);
        if (record is null)
        {
            return Reading.Missing<AgedPart<T>>($"no recorded full run has sampled {what} yet");
        }

        var value = part(record)!;
        var reason = unavailable(value) ?? string.Empty;
        return reason.Length == 0
            ? Reading.Of(new AgedPart<T>(value, record.RunId, sampledAt(value), now - sampledAt(value)))
            : Reading.Missing<AgedPart<T>>($"the last full run that tried ({record.RunId}) could not sample {what}: {reason}");
    }

    private static IReadOnlyList<RunRecord> NewestFirst(byte[] bytes) =>
        [.. System.Text.Encoding.UTF8.GetString(bytes).Split('\n').Reverse().SelectMany(Parse)];

    private static IEnumerable<RunRecord> Parse(string line)
    {
        if (line.Trim().Length == 0)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(line, WslCareJsonContext.Compact.RunRecord) is { } record ? [record] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static LastSlowParts Unreadable(string reason) =>
        new(Reading.Missing<AgedPart<ContainerStatsSample>>(reason), Reading.Missing<AgedPart<WindowsClockSample>>(reason));
}
