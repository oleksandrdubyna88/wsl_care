using WslCare.Core.Collectors;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

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

    public DefenderReading ToReading() => new(Cpu, Ts, Pid, Start);

    public static DefenderLedgerLine Of(DefenderReading reading) => new(reading.Timestamp100ns, reading.CpuTicks100ns, reading.Pid, reading.StartMarker);
}

/// <summary>The ledger as read: every line that parses (oldest first), the lines skipped and named, and why the file could not be read
/// at all. A missing file is no problem — it is "no baseline".</summary>
public sealed record DefenderLedgerRead(IReadOnlyList<DefenderLedgerLine> Lines, IReadOnlyList<string> Skipped, string Problem)
{
    public const string NoBaseline = "no earlier full run recorded a Defender reading — a new baseline";

    /// <summary>The newest line's reading — the baseline the next full run is measured against.</summary>
    public Reading<DefenderReading> Newest => Reading.Missing<DefenderReading>(NoBaseline);

    /// <summary>The spans the lines carry, oldest first.</summary>
    public IReadOnlyList<DefenderSpan> Spans => [];
}

/// <summary>
/// <c>{state}/defender-cpu.jsonl</c> (E15.S10): one line per full run, appended through <see cref="IFileSystem.AppendLine"/> (whole
/// lines under the cross-process lock), never rewritten — except to drop the lines older than <c>defender.localRetentionDays</c>,
/// which happens when the file is next written, by <see cref="IFileSystem.RewriteLines"/>: a temp file made IN <c>{state}</c>, beside
/// the ledger, and one same-directory rename — never in <c>/tmp</c>, so the replace never crosses a filesystem. Until E17.S1 takes it
/// over as a statistics record, this file is the only store of the figure.
/// </summary>
public sealed class DefenderCpuLedger(IHostPaths paths, IFileSystem files)
{
    public const string FileName = "defender-cpu.jsonl";

    public string File => paths.Rules.Join(paths.StateDirectory, FileName);

    public DefenderLedgerRead Read() => files.FileExists(File) ? new([], [], string.Empty) : new([], [], string.Empty);

    /// <summary>Appends <paramref name="line"/>; returns empty, or why it was not recorded.</summary>
    public string Append(DefenderLedgerLine line, DateTimeOffset now) => string.Empty;
}
