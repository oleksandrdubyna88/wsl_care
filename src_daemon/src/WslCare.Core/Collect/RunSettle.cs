using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Health;
using WslCare.Core.Hosting;
using WslCare.Core.Thresholds;

namespace WslCare.Core.Collect;

/// <summary>The settle step's three keys (PLAN_boot_settle.md): <c>timer.bootDelayMinutes</c>, <c>timer.busyWaitMinutes</c>,
/// <c>timer.busyCheckSeconds</c>.</summary>
public sealed record SettleSettings(TimeSpan BootDelay, TimeSpan BusyWait, TimeSpan BusyCheck)
{
    public static SettleSettings From(EffectiveConfig config) => new(
        TimeSpan.FromMinutes(config.Int(ConfigKeys.Timer.BootDelayMinutes)),
        TimeSpan.FromMinutes(config.Int(ConfigKeys.Timer.BusyWaitMinutes)),
        TimeSpan.FromSeconds(config.Int(ConfigKeys.Timer.BusyCheckSeconds)));
}

/// <summary>What the settle step reads: the machine's uptime, and S6's busy judgement now.</summary>
public sealed record SettleProbe(Func<Reading<TimeSpan>> Uptime, Func<BusyJudgement> Busy)
{
    /// <summary>The distro's: <c>/proc/uptime</c> and <c>/proc/pressure/*</c> under <paramref name="paths"/>, judged by S6's keys.</summary>
    public static SettleProbe ForDistro(IFileSystem files, LinuxHostPaths paths, BusyLimits limits) => new(
        () => ProcText.Read(files, $"{paths.ProcRoot}/uptime").Bind(HealthParsers.Uptime),
        () => MachineBusy.Judge(PressureFile.ReadSet(files, paths), limits));
}

/// <summary>How long a timer run waited before it started, whether the machine was still busy when the bound ran out, and what
/// could not be read.</summary>
public sealed record RunSettled(TimeSpan BootWait, TimeSpan BusyWait, bool BusyAtEnd, IReadOnlyList<string> Notes)
{
    public static readonly RunSettled None = new(TimeSpan.Zero, TimeSpan.Zero, false, []);
}

/// <summary>
/// The settle step of a TIMER run (PLAN_boot_settle.md) — run before the run lock is taken and before <c>running.json</c> is
/// written, so a waiting run blocks nothing. First the boot: while the machine has been up less than <c>timer.bootDelayMinutes</c>
/// it waits until it has (2026-10-10: the catch-up ran 07:44–07:48Z while Docker started 12 containers). Then, at any time, while S6
/// (<see cref="MachineBusy"/>) says busy, it waits <c>timer.busyCheckSeconds</c> and asks again, for at most
/// <c>timer.busyWaitMinutes</c>; then it runs anyway — a wait never skips the run. An unread uptime or an unread busy signal is no
/// wait (S6: "cannot say" means go), and each is noted. Each wait is said through <c>say</c> before it starts (the run's log: a
/// waiting run is never silent). A run that took the lock DURING the wait does not turn the timer run away: the settled run waits for
/// the lock for what is left of the busy bound (<see cref="LockWaitAfter"/>, coai code round aebbaafd).
/// </summary>
public static class RunSettle
{
    public static async Task<RunSettled> WaitAsync(SettleProbe probe, SettleSettings settings, Func<TimeSpan, CancellationToken, Task> wait, CancellationToken cancellationToken, Action<string>? say = null)
    {
        var notes = new List<string>();
        var tell = say ?? (static _ => { });
        var boot = BootWait(probe.Uptime(), settings.BootDelay, notes);
        if (boot > TimeSpan.Zero)
        {
            tell(FormattableString.Invariant($"collect --timer: waiting {Math.Ceiling(boot.TotalMinutes)} min for the boot to settle (timer.bootDelayMinutes {settings.BootDelay.TotalMinutes})"));
            await wait(boot, cancellationToken).ConfigureAwait(false);
        }

        var (busyWait, busyAtEnd) = await BusyAsync(probe, settings, wait, notes, tell, cancellationToken).ConfigureAwait(false);
        return new RunSettled(boot, busyWait, busyAtEnd, notes);
    }

    /// <summary>How long a settled timer run waits for the run lock: what is left of <c>timer.busyWaitMinutes</c> after the busy wait,
    /// never less than the accepted-run wait it had before (<c>requests.lockWaitSeconds</c>) — so a manual run that took the lock
    /// during the settle wait delays the timer run instead of turning it away, inside the bound the run budget already counts.</summary>
    public static TimeSpan LockWaitAfter(RunSettled settled, SettleSettings settings, TimeSpan accepted)
    {
        var left = settings.BusyWait - settled.BusyWait;
        return left > accepted ? left : accepted;
    }

    /// <summary>How long until the machine has been up the boot delay; zero when it has, when the delay is off, or when the uptime
    /// cannot be read (noted — never read as a fresh boot).</summary>
    private static TimeSpan BootWait(Reading<TimeSpan> uptime, TimeSpan delay, List<string> notes)
    {
        if (uptime is not Reading<TimeSpan>.Available { Value: var up })
        {
            notes.Add($"no boot wait: {uptime.ReasonOrEmpty}");
            return TimeSpan.Zero;
        }

        return up < delay ? delay - up : TimeSpan.Zero;
    }

    /// <summary>The busy wait: ask, and while busy wait one step (never past the bound) and ask again. Returns how long it waited
    /// and whether the machine was still busy when the bound ran out.</summary>
    private static async Task<(TimeSpan Waited, bool BusyAtEnd)> BusyAsync(SettleProbe probe, SettleSettings settings, Func<TimeSpan, CancellationToken, Task> wait, List<string> notes, Action<string> tell, CancellationToken cancellationToken)
    {
        var waited = TimeSpan.Zero;
        while (StillBusy(probe, notes))
        {
            if (waited >= settings.BusyWait)
            {
                return (waited, settings.BusyWait > TimeSpan.Zero);
            }

            var step = Min(settings.BusyCheck, settings.BusyWait - waited);
            if (waited == TimeSpan.Zero)
            {
                tell(FormattableString.Invariant($"collect --timer: waiting while {notes[^1]} (at most timer.busyWaitMinutes {settings.BusyWait.TotalMinutes})"));
            }

            await wait(step, cancellationToken).ConfigureAwait(false);
            waited += step;
        }

        return (waited, false);
    }

    /// <summary>Busy means wait; calm means go; "cannot say" means go, and the note says why (S6's rule). A note is kept once,
    /// however many times it is asked.</summary>
    private static bool StillBusy(SettleProbe probe, List<string> notes)
    {
        var judgement = probe.Busy();
        var why = judgement.State switch
        {
            BusyState.Busy => "the machine is busy: " + string.Join("; ", judgement.Reasons.Select(r => FormattableString.Invariant($"{r.Resource} {r.Window} {r.Value:0.##} > {r.Limit:0.##} ({r.Key})"))),
            BusyState.Unknown => "the busy signal cannot say, so the run goes: " + string.Join("; ", judgement.Unread),
            _ => string.Empty,
        };
        if (why.Length > 0 && !notes.Contains(why))
        {
            notes.Add(why);
        }

        return judgement.State == BusyState.Busy;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}

/// <summary>The settle step as a run's detail records it (PLAN_boot_settle.md; additive, absent on a run the timer did not start).</summary>
public sealed record RunSettledReport(long BootWaitSeconds, long BusyWaitSeconds, bool BusyAtEnd, IReadOnlyList<string> Notes)
{
    public static RunSettledReport From(RunSettled settled) =>
        new((long)settled.BootWait.TotalSeconds, (long)settled.BusyWait.TotalSeconds, settled.BusyAtEnd, settled.Notes);
}
