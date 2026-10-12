using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Health;

namespace WslCare.Core.Thresholds;

/// <summary>
/// The three verdicts of PLAN_windows_time_guard.md D4, PURE: <c>clock.timeService</c> (is the Windows Time service running,
/// and how it starts), <c>clock.reference</c> (WHICH clock is wrong, against an independent reference), <c>clock.fight</c>
/// (are two time-keepers setting the distro's clock against each other). Each names the fix in its reason.
/// </summary>
public static class ClockVerdicts
{
    public const string TimeServiceId = "clock.timeService";
    public const string ReferenceId = "clock.reference";
    public const string FightId = "clock.fight";

    /// <summary>Who sets the one clock every WSL distro shares — measured 2026-10-12 (research/2026-10-12_wsl_clock_fight.md),
    /// said in ONE place so <c>clock.fight</c> and <c>clock.jumps</c> never explain the same capture two ways (code round).</summary>
    public const string SharedClockWriters = "the one clock every WSL distro shares has up to three writers — Docker Desktop's /initd (a step to Windows' time every 30 s while it runs, ≈ 480 per 4 h), WSL's own chronyd (the Hyper-V host clock) and timesyncd (NTP) — and they disagree by Windows' offset to NTP";

    public static double TimeJumpsBackWarnPer4h => Tuning.Current.Int(ConfigKeys.Thresholds.TimeJumpsBackWarnPer4h);

    public static IReadOnlyList<Verdict> Evaluate(HealthSample health, EffectiveConfig config)
    {
        var judgement = health.ClockJudgement;
        return [TimeService(health.WindowsClock.TimeServiceReading, judgement, config), Reference(judgement, config), Fight(health.TimeJumpsBack, config.Int(ConfigKeys.Thresholds.TimeJumpsBackWarnPer4h))];
    }

    /// <summary>Stopped is a WARNING while the clock agrees (on a workgroup PC Windows trigger-starts it, measured 2026-10-08)
    /// and CRITICAL only while the reference names Windows as the wrong clock.</summary>
    public static Verdict TimeService(Reading<WindowsTimeService> service, ClockJudgement judgement, EffectiveConfig config)
    {
        const string limit = "warn: not running, or StartType Manual (clock.manualStartWarns); critical: not running while clock.reference names Windows";
        return service switch
        {
            Reading<WindowsTimeService>.Available { Value: var s } => Judged(s, judgement, config.Bool(ConfigKeys.Clock.ManualStartWarns), limit),
            var unknown => new(TimeServiceId, Level.Unknown, string.Empty, limit, unknown.ReasonOrEmpty),
        };
    }

    /// <summary>An EMPTY status (Get-Service answered nothing) is not a fact about the service: unknown (own code review #3).
    /// An empty start type only skips the manual-start rule.</summary>
    private static Verdict Judged(WindowsTimeService s, ClockJudgement judgement, bool manualWarns, string limit) => s switch
    {
        { Status.Length: 0 } => new(TimeServiceId, Level.Unknown, s.Text, limit, "the clock probe could not read the Windows Time service (Get-Service answered nothing)"),
        { IsRunning: false } when judgement.WindowsIsWrong => new(TimeServiceId, Level.Critical, s.Text, limit, $"the Windows Time service is not running and {judgement.Reason}"),
        { IsRunning: false } => new(TimeServiceId, Level.Warn, s.Text, limit, $"the Windows Time service is not running, so nothing corrects Windows' clock; on a workgroup PC Windows starts it by a trigger, so this alone is not a fault while the clock agrees — {ClockStandings.WindowsFix}"),
        { StartsAutomatically: false, StartType.Length: > 0 } when manualWarns => new(TimeServiceId, Level.Warn, s.Text, limit, "the Windows Time service runs but does not start Automatic (Windows' default is Manual): Windows or other software can stop it again — the extension's Start Windows Time sets Automatic, which does NOT restart it after a stop; only the scheduled guard (planned) does"),
        _ => new(TimeServiceId, Level.Ok, s.Text, limit, "the Windows Time service is running"),
    };

    public static Verdict Reference(ClockJudgement judgement, EffectiveConfig config)
    {
        var limit = string.Create(CultureInfo.InvariantCulture, $"critical: Windows more than {config.Int(ConfigKeys.Clock.ReferenceToleranceSeconds)} s off the reference; warn: the distro (clock.referenceToleranceSeconds)");
        var value = judgement.Standing == ClockStanding.Unknown
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $"{Name(judgement.Standing)}: Windows {judgement.WindowsMinusReferenceSeconds:+0.0;-0.0} s, the distro {judgement.WslMinusReferenceSeconds:+0.0;-0.0} s");
        return new(ReferenceId, LevelOf(judgement.Standing), value, limit, judgement.Reason);
    }

    private static Level LevelOf(ClockStanding standing) => standing switch
    {
        ClockStanding.WindowsSlow or ClockStanding.WindowsFast => Level.Critical,
        ClockStanding.WslWrong => Level.Warn,
        ClockStanding.Agree => Level.Ok,
        _ => Level.Unknown,
    };

    /// <summary>The wire name of a standing (<c>windowsSlow</c>, …) — the enum's own JSON spelling.</summary>
    public static string Name(ClockStanding standing) => char.ToLowerInvariant(standing.ToString()[0]) + standing.ToString()[1..];

    /// <summary>The fight judged against the PROCESS tuning — the full run passes its effective configuration instead.</summary>
    public static Verdict Fight(Reading<int> jumpsBack) => Fight(jumpsBack, (int)TimeJumpsBackWarnPer4h);

    /// <summary>The fight judged against <paramref name="warnAbove"/>, the SAME effective <c>thresholds.timeJumpsBackWarnPer4h</c>
    /// <c>clock.jumps</c> uses (code round 2: the two read different sources).</summary>
    public static Verdict Fight(Reading<int> jumpsBack, int warnAbove)
    {
        var limit = string.Create(CultureInfo.InvariantCulture, $"warn > {warnAbove} in the last 4 h of this boot (thresholds.timeJumpsBackWarnPer4h)");
        return jumpsBack switch
        {
            Reading<int>.Available { Value: var n } when n > warnAbove => new(
                FightId,
                Level.Warn,
                string.Create(CultureInfo.InvariantCulture, $"{n} in 4 h"),
                limit,
                string.Create(CultureInfo.InvariantCulture, $"the distro's clock was set backwards {n} times in 4 h and journald rotated its file each time, which is why the journal history is short; {SharedClockWriters}; clock.reference names the wrong clock (research/2026-10-12_wsl_clock_fight.md)")),
            Reading<int>.Available { Value: var n } => new(FightId, Level.Ok, string.Create(CultureInfo.InvariantCulture, $"{n} in 4 h"), limit, "journald's \"Time jumped backwards\" in the last 4 h of this boot (monotonic clock)"),
            var unknown => new(FightId, Level.Unknown, string.Empty, limit, unknown.ReasonOrEmpty),
        };
    }
}
