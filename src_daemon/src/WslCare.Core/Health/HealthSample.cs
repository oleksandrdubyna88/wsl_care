using WslCare.Core.Collectors;
using WslCare.Core.Records;
using WslCare.Core.Systemd;

namespace WslCare.Core.Health;

/// <summary>The kernel's distress signals since the last run (plan §4.1): allocation failures and OOM kills, and the
/// first few lines that said so.</summary>
public sealed record KernelSignals(int AllocationFailures, int OomKills, IReadOnlyList<string> Lines);

/// <summary>A collector that writes a file per day (sysstat, atop): the newest file and when it was last written.</summary>
public sealed record CollectorFreshness(string File, DateTimeOffset LastWrite);

/// <summary>The <c>.wslconfig</c> audit of plan §4.5: where the file is, what it sets, and what is wrong with it.</summary>
/// <param name="Present">Whether the file exists — absent means WSL's defaults (memory = half the host's RAM).</param>
public sealed record WslConfigAudit(string File, bool Present, WslConfigSettings Settings, IReadOnlyList<string> Warnings);

/// <summary>
/// One full run's look at the distro's health (plan §4.5) — every part a <see cref="Reading{T}"/>, so a part a tool
/// could not answer is unavailable WITH the reason, never 0 (plan §15b #7).
/// </summary>
/// <param name="Since">The instant the "since the last run" counts start at: the previous run's start, or four hours
/// ago when there is none (the timer's period, plan §8).</param>
/// <param name="ClockJumps">systemd-resolved's "Clock change detected" lines since <paramref name="Since"/> — the
/// counter of F4 (≈1 700 a day, measured 2026-10-02).</param>
/// <param name="OomDaemon">earlyoom or systemd-oomd is active (plan §4.5, OOM forensics).</param>
/// <param name="WindowsProfile">The Windows user profile as this side sees it (<c>/mnt/c/Users/…</c>), or why not —
/// what the <c>.wslconfig</c> audit and Docker Desktop's <c>daemon.json</c> are read through.</param>
public sealed record HealthSample(
    DateTimeOffset Since,
    Reading<IReadOnlyList<FailedUnit>> FailedUnits,
    Reading<long> JournalBytes,
    Reading<DateTimeOffset> JournalOldestEntry,
    Reading<int> ClockJumps,
    Reading<KernelSignals> Kernel,
    Reading<TimeSync> TimeSync,
    Reading<SystemdUnit> WslPro,
    Reading<SystemdUnit> FstrimTimer,
    Reading<bool> RootDiscard,
    Reading<TimeSpan> Uptime,
    Reading<CollectorFreshness> Sysstat,
    Reading<CollectorFreshness> Atop,
    Reading<bool> OomDaemon,
    WindowsClockSample WindowsClock,
    Reading<string> WindowsProfile,
    Reading<WslConfigAudit> WslConfig)
{
    /// <summary>The independent clock reference (PLAN_windows_time_guard.md D2), measured right after the Windows clock.</summary>
    public Reading<ClockReference> ClockReference { get; init; } = Reading.Missing<ClockReference>("not read");

    /// <summary>journald's "Time jumped backwards" in the last 4 h of this boot, counted on the monotonic clock (D4).</summary>
    public Reading<int> TimeJumpsBack { get; init; } = Reading.Missing<int>("not read");

    /// <summary>The whole sample unavailable for one reason — the Windows binary, which has no systemd to ask.</summary>
    public static HealthSample Unavailable(DateTimeOffset since, string reason, WindowsClockSample clock, Reading<string> profile, Reading<WslConfigAudit> wslConfig) =>
        new(
            since,
            Reading.Missing<IReadOnlyList<FailedUnit>>(reason),
            Reading.Missing<long>(reason),
            Reading.Missing<DateTimeOffset>(reason),
            Reading.Missing<int>(reason),
            Reading.Missing<KernelSignals>(reason),
            Reading.Missing<TimeSync>(reason),
            Reading.Missing<SystemdUnit>(reason),
            Reading.Missing<SystemdUnit>(reason),
            Reading.Missing<bool>(reason),
            Reading.Missing<TimeSpan>(reason),
            Reading.Missing<CollectorFreshness>(reason),
            Reading.Missing<CollectorFreshness>(reason),
            Reading.Missing<bool>(reason),
            clock,
            profile,
            wslConfig);
}
