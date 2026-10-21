using System.Text;
using System.Text.Json;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Records;

namespace WslCare.Core.Health;

/// <summary>
/// One line of <c>{state}/defender-cpu.jsonl</c> (E15.S10): the reading of one full run (its instant, CPU ticks, pid and start marker —
/// all in the units the script printed) and, when the run before it read the SAME process, the span between them. A JSON edge record:
/// <see cref="From"/> and <see cref="Cores"/> are absent for a first reading and for a restart.
/// </summary>
/// <param name="Ts"><c>Timestamp_Sys100NS</c> of the read — FILETIME, the instant a span ends and a line ages by.</param>
/// <param name="Cpu"><c>PercentProcessorTime</c> of the read.</param>
/// <param name="Pid">The process id.</param>
/// <param name="Start">The process's start marker.</param>
public sealed record DefenderLedgerLine(long Ts, long Cpu, int Pid, long Start)
{
    /// <summary>The span's start (FILETIME): the previous line's <see cref="Ts"/>; absent when there is no same-process span.</summary>
    public long? From { get; init; }

    /// <summary>The span's average cores; absent when there is no same-process span.</summary>
    public double? Cores { get; init; }

    /// <summary>The last FILETIME a <see cref="DateTime"/> can name — a line past it cannot be read as an instant.</summary>
    private static readonly long LastFileTime = DateTime.MaxValue.ToFileTimeUtc();

    /// <summary>A line a reader may use: the deserializer runs no defaults and checks no range (C# doctrine §4a), so a line with a missing
    /// field (a pid of 0, a start marker of 0) or an instant no clock reads is no line.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool WellFormed => InstantWellFormed && ProcessWellFormed && SpanWellFormed;

    public DefenderReading ToReading() => new(Cpu, Ts, Pid, Start);

    public static DefenderLedgerLine Of(DefenderReading reading) => new(reading.Timestamp100ns, reading.CpuTicks100ns, reading.Pid, reading.StartMarker);

    private bool InstantWellFormed => Ts is > 0 && Ts <= LastFileTime;

    private bool ProcessWellFormed => Cpu >= 0 && Pid > 0 && Start > 0;

    /// <summary>Both span members or neither; the span starts before it ends, and its cores are a finite non-negative number.</summary>
    private bool SpanWellFormed => (From, Cores) switch
    {
        (null, null) => true,
        ({ } from, { } cores) => IsSpan(from, cores),
        _ => false,
    };

    private bool IsSpan(long from, double cores) => from is > 0 && from < Ts && double.IsFinite(cores) && cores >= 0;
}

/// <summary>The ledger as read: every line that parses (oldest first), the lines skipped and named, and why the file could not be read
/// at all. A missing file is no problem — it is "no baseline".</summary>
public sealed record DefenderLedgerRead(IReadOnlyList<DefenderLedgerLine> Lines, IReadOnlyList<string> Skipped, string Problem)
{
    public const string NoBaseline = "no earlier full run recorded a Defender reading — a new baseline";

    /// <summary>The newest line's reading — the baseline the next full run is measured against; why there is none otherwise.</summary>
    public Reading<DefenderReading> Newest => Lines is [.., var newest]
        ? Reading.Of(newest.ToReading())
        : Reading.Missing<DefenderReading>(Problem.Length > 0 ? Problem : NoBaseline);

    /// <summary>The spans the lines carry, oldest first.</summary>
    public IReadOnlyList<DefenderSpan> Spans => [.. Lines.SelectMany(SpanOf)];

    /// <summary>The text of a ledger: every COMPLETE line (a trailing segment without its newline is a write in progress, not a line —
    /// <see cref="WslCare.Core.Files.LineFiles"/>), the ones that do not parse skipped and named by their number in the file.</summary>
    public static DefenderLedgerRead Parse(string text, string name)
    {
        var parsed = Numbered(text).Select(l => (l.Number, Line: ParseLine(l.Text))).ToList();
        return new(
            [.. parsed.SelectMany(p => p.Line)],
            [.. parsed.Where(p => p.Line.Count == 0).Select(p => $"line {p.Number} of {name} does not parse and is skipped")],
            string.Empty);
    }

    /// <summary>One line's text as a line — none when it does not parse or is not well formed.</summary>
    public static IReadOnlyList<DefenderLedgerLine> ParseLine(string text)
    {
        try
        {
            return JsonSerializer.Deserialize(text, WslCareJsonContext.Compact.DefenderLedgerLine) is { WellFormed: true } line ? [line] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The span a line carries — none for a first reading and for a restart.</summary>
    public static IEnumerable<DefenderSpan> SpanOf(DefenderLedgerLine line) =>
        line is { From: { } from, Cores: { } cores } ? [new DefenderSpan(DefenderCpu.Instant(from), DefenderCpu.Instant(line.Ts), cores)] : [];

    private static IEnumerable<(int Number, string Text)> Numbered(string text)
    {
        var end = text.LastIndexOf('\n');
        return end < 0 ? [] : text[..end].Split('\n').Select((line, index) => (index + 1, line)).Where(l => l.line.Trim().Length > 0);
    }
}

/// <summary>
/// <c>{state}/defender-cpu.jsonl</c> (E15.S10): one line per full run, appended through <see cref="IFileSystem.AppendLine"/> (whole
/// lines under the cross-process lock), never rewritten — except to drop the lines older than <c>defender.localRetentionDays</c>,
/// which happens when the file is next written, by <see cref="IFileSystem.RewriteLines"/>: a temp file made IN <c>{state}</c>, beside
/// the ledger, and one same-directory rename — never in <c>/tmp</c>, so the replace never crosses a filesystem. Until E17.S1 takes it
/// over as a statistics record, this file is the only store of the figure.
/// </summary>
/// <remarks>The size is bounded by AGE: a full run is started at most once an hour by the timer (<c>timer.periodHours</c>, 1–24) and a
/// manual <c>collect</c> adds a few a day; a line is about 160 bytes, so 30 runs a day for the 400 days kept is about 1.9 MB at the most.
/// A burst of manual runs grows the file linearly and the next write after the retention trims it.</remarks>
public sealed class DefenderCpuLedger(IHostPaths paths, IFileSystem files)
{
    public const string FileName = "defender-cpu.jsonl";

    private const string Action = "defender-cpu-retention";

    public string File => paths.Rules.Join(paths.StateDirectory, FileName);

    /// <summary>The ledger; a missing file is no baseline, an unreadable one says why — never an error.</summary>
    public DefenderLedgerRead Read() => files.ReadFile(File, RootFileCaps.History) switch
    {
        FileReadResult.Content content => DefenderLedgerRead.Parse(Encoding.UTF8.GetString(content.Bytes), FileName),
        FileReadResult.Missing => new([], [], string.Empty),
        FileReadResult.Unreadable unreadable => new([], [], $"{File} could not be read: {unreadable.Reason}"),
        _ => throw new System.Diagnostics.UnreachableException("FileReadResult is a closed set"),
    };

    /// <summary>Drops the lines past the retention, then appends <paramref name="line"/>; empty when both went well, else what did not
    /// (an old line that could not be dropped does not stop the new one being recorded).</summary>
    public string Append(DefenderLedgerLine line, DateTimeOffset now)
    {
        try
        {
            files.CreateDirectory(paths.StateDirectory);
            var trimmed = Trim(now);
            files.AppendLine(File, Serialise(line), RunRecordWriter.LockTimeout);
            return trimmed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException)
        {
            return $"{File} could not be written: {e.Message}";
        }
    }

    /// <summary>The line as written: one compact JSON line, its cores to six decimals.</summary>
    public static string Serialise(DefenderLedgerLine line) =>
        JsonSerializer.Serialize(line with { Cores = line.Cores is { } cores ? Math.Round(cores, 6) : null }, WslCareJsonContext.Compact.DefenderLedgerLine);

    /// <summary>The lines older than the retention dropped, under the append lock; a line that does not parse is never judged (kept).</summary>
    private string Trim(DateTimeOffset now)
    {
        var cutoff = now - TimeSpan.FromDays(Tuning.Current.Int(ConfigKeys.Defender.LocalRetentionDays));
        var verdict = files.RewriteLines(File, lines => [.. lines.Where(l => !Aged(l, cutoff))], new DeletionScope(paths.StateDirectory, Action), RunRecordWriter.LockTimeout);
        return verdict is DeletionVerdict.Refused refused ? $"the ledger's old lines could not be dropped: {refused.Reason}" : string.Empty;
    }

    private static bool Aged(string line, DateTimeOffset cutoff) =>
        DefenderLedgerRead.ParseLine(line) is [var parsed] && DefenderCpu.Instant(parsed.Ts) < cutoff;
}
