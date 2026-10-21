using WslCare.Core.Collectors;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Health;

/// <summary>What one full run made of Windows Defender's CPU: the usage since the previous full run, the week it joins, and — when the
/// reading was not recorded in the ledger, or the file could not be put in order — why (<see cref="string.Empty"/> when all went well).</summary>
public sealed record DefenderOutcome(Reading<DefenderUsage> Usage, Reading<DefenderWeek> Week, string LedgerNote);

/// <summary>
/// E15.S10: the step between the script's reading and the verdict — the ledger read, the two-point rule
/// (<see cref="DefenderCpu.Between"/> against the NEWEST line, which is the previous full run's reading), the ledger append, and the week
/// the new span joins. Only the distro's full run does it: the Windows binary runs no script, so it reads and writes no ledger.
/// </summary>
public static class DefenderCpuRun
{
    /// <summary>Why a run that may not record leaves the reading out of the ledger.</summary>
    public const string ReadOnlyRun = "this run may not record: its reading is not in defender-cpu.jsonl, so the next run measures against the one before";

    public static DefenderOutcome Apply(IHostPaths paths, IFileSystem files, DefenderSample sample, bool mayRecord)
    {
        if (paths is not LinuxHostPaths)
        {
            return new DefenderOutcome(Reading.Missing<DefenderUsage>(sample.Read.ReasonOrEmpty), Reading.Missing<DefenderWeek>(sample.Read.ReasonOrEmpty), string.Empty);
        }

        var ledger = new DefenderCpuLedger(paths, files);
        var read = ledger.Read();
        return sample.Read is Reading<DefenderReading>.Available { Value: var now }
            ? Measured(ledger, read, sample.SampledAt, now, mayRecord)
            : new DefenderOutcome(Reading.Missing<DefenderUsage>(sample.Read.ReasonOrEmpty), DefenderCpu.Week(read.Spans, sample.SampledAt), Noted(read, string.Empty));
    }

    /// <summary>A reading: against the newest line, then recorded (unless the run may not), with the week its span joins.</summary>
    private static DefenderOutcome Measured(DefenderCpuLedger ledger, DefenderLedgerRead read, DateTimeOffset at, DefenderReading now, bool mayRecord)
    {
        var usage = read.Newest.Bind(previous => DefenderCpu.Between(previous, now));
        var line = LineOf(now, read.Newest, usage);
        var note = mayRecord ? ledger.Append(line, at) : ReadOnlyRun;
        return new DefenderOutcome(usage, DefenderCpu.Week([.. read.Spans, .. DefenderLedgerRead.SpanOf(line)], at), Noted(read, note));
    }

    /// <summary>The line to record: the reading, and — when it continues the previous one — the span between them.</summary>
    private static DefenderLedgerLine LineOf(DefenderReading now, Reading<DefenderReading> previous, Reading<DefenderUsage> usage) =>
        (previous, usage) switch
        {
            (Reading<DefenderReading>.Available { Value: var before }, Reading<DefenderUsage>.Available { Value: var used }) =>
                DefenderLedgerLine.Of(now) with { From = before.Timestamp100ns, Cores = used.Cores },
            _ => DefenderLedgerLine.Of(now),
        };

    /// <summary>What the run says about the file: the write's note, the lines skipped on the read, why it could not be read at all.</summary>
    private static string Noted(DefenderLedgerRead read, string note)
    {
        string[] parts = [note, read.Problem, read.Skipped > 0 ? $"{read.Skipped} {(read.Skipped == 1 ? "line" : "lines")} skipped" : string.Empty];
        return string.Join("; ", parts.Where(n => n.Length > 0));
    }
}
