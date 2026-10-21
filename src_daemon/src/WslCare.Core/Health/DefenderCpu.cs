using WslCare.Core.Collectors;

namespace WslCare.Core.Health;

/// <summary>One read of Windows Defender's engine (<c>MsMpEng</c>) from the performance counters' raw class
/// (<c>Win32_PerfRawData_PerfProc_Process</c>), as the third fixed script prints it (<see cref="DefenderCpuCommands"/>).</summary>
/// <param name="CpuTicks100ns"><c>PercentProcessorTime</c>: the process's CPU time summed over ALL cores, in 100 ns units.</param>
/// <param name="Timestamp100ns"><c>Timestamp_Sys100NS</c>: the sample's instant, in 100 ns units (FILETIME: since 1601-01-01 UTC).</param>
/// <param name="Pid"><c>IDProcess</c>.</param>
/// <param name="StartMarker"><c>ElapsedTime</c>: on this RAW class it is the process's START instant on the same FILETIME clock, not an
/// elapsed duration — constant across reads of one process while <paramref name="Timestamp100ns"/> advances (six reads measured
/// 2026-10-16, research/2026-10-16_defender_cpu_cost.md). Windows reuses pids and an engine update restarts MsMpEng without a
/// reboot; the start instant tells two processes of one pid apart.</param>
public sealed record DefenderReading(long CpuTicks100ns, long Timestamp100ns, int Pid, long StartMarker);

/// <summary>What Defender's engine used between two reads of the SAME process.</summary>
/// <param name="Cores">CPU time over wall time — summed over all cores, so the ratio is cores (1.0 = one core busy throughout).</param>
/// <param name="Covered">The wall time between the two reads.</param>
public sealed record DefenderUsage(double Cores, TimeSpan Covered);

/// <summary>One stretch of one process's life between two full runs, as the ledger keeps it.</summary>
public sealed record DefenderSpan(DateTimeOffset Start, DateTimeOffset End, double Cores);

/// <summary>The spans of the last 7 days summed: core-hours per week, over how many hours they cover, and whether the week is
/// extrapolated from fewer than 168 covered hours.</summary>
public sealed record DefenderWeek(double CoreHoursPerWeek, double CoveredHours, bool Extrapolated);

/// <summary>
/// E15.S10: the PURE rule over Windows Defender's raw counters — usage is computed ONLY between two reads of the same process (the
/// same pid AND the same start marker); every other pair is a new baseline. It never invents usage: another process, a counter that
/// went down, a non-positive interval or a missing read says why instead of giving a figure.
/// </summary>
public static class DefenderCpu
{
    public const string Restarted = "Defender restarted — a new baseline";

    /// <summary>The 100 ns units in one second (FILETIME).</summary>
    public const long TicksPerSecond = TimeSpan.TicksPerSecond;

    private const string NotInOrder = "the two Defender reads are not in order (the interval between them is not positive) — a new baseline";

    private const string CounterWentDown = "Defender's CPU counter went down between the two reads — a new baseline";

    private const string NoSpan = "no Defender span of the last 7 days: the engine was read once, restarted, or not read";

    /// <summary>The definition of the figure "per week" — a unit, not a tuning.</summary>
    private const int DaysPerWeek = 7;

    private const int HoursPerDay = 24;

    private const int HoursPerWeek = HoursPerDay * DaysPerWeek;

    /// <summary>The cores <paramref name="now"/> used since <paramref name="previous"/>, or why no figure is given.</summary>
    public static Reading<DefenderUsage> Between(DefenderReading previous, DefenderReading now) =>
        SameProcess(previous, now) ? Counted(previous, now) : Reading.Missing<DefenderUsage>(Restarted);

    /// <summary>The spans of the 7 days ending at <paramref name="now"/>, summed (clipped to that window); a restart's gap is no span, so
    /// it adds no covered hours. The week is EXTRAPOLATED — core-hours per covered hour, times 168 — when the spans cover fewer hours.</summary>
    public static Reading<DefenderWeek> Week(IReadOnlyList<DefenderSpan> spans, DateTimeOffset now)
    {
        var inside = spans.Select(s => Clipped(s, now - TimeSpan.FromDays(DaysPerWeek), now)).Where(c => c.Hours > 0).ToList();
        var covered = inside.Sum(c => c.Hours);
        return covered > 0
            ? Reading.Of(new DefenderWeek(inside.Sum(c => c.Hours * c.Cores) / covered * HoursPerWeek, covered, covered < HoursPerWeek))
            : Reading.Missing<DefenderWeek>(NoSpan);
    }

    /// <summary>A FILETIME as the UTC instant it names.</summary>
    public static DateTimeOffset Instant(long fileTime) => new(DateTime.FromFileTimeUtc(fileTime), TimeSpan.Zero);

    private static bool SameProcess(DefenderReading previous, DefenderReading now) =>
        previous.Pid == now.Pid && previous.StartMarker == now.StartMarker;

    private static Reading<DefenderUsage> Counted(DefenderReading previous, DefenderReading now)
    {
        var interval = now.Timestamp100ns - previous.Timestamp100ns;
        var cpu = now.CpuTicks100ns - previous.CpuTicks100ns;
        if (interval <= 0)
        {
            return Reading.Missing<DefenderUsage>(NotInOrder);
        }

        return cpu < 0
            ? Reading.Missing<DefenderUsage>(CounterWentDown)
            : Reading.Of(new DefenderUsage((double)cpu / interval, TimeSpan.FromTicks(interval)));
    }

    /// <summary>The part of <paramref name="span"/> inside the window, in hours, with its cores.</summary>
    private static (double Hours, double Cores) Clipped(DefenderSpan span, DateTimeOffset from, DateTimeOffset to)
    {
        var start = span.Start > from ? span.Start : from;
        var end = span.End < to ? span.End : to;
        return ((end - start).TotalHours, span.Cores);
    }
}
