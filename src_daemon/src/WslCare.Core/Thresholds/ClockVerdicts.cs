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

    public static double TimeJumpsBackWarnPer4h => Tuning.Current.Int(ConfigKeys.Thresholds.TimeJumpsBackWarnPer4h);

    public static IReadOnlyList<Verdict> Evaluate(HealthSample health, EffectiveConfig config)
    {
        var judgement = ClockStandings.Judge(health.WindowsClock, health.ClockReference, config.Int(ConfigKeys.Clock.ReferenceToleranceSeconds));
        return [TimeService(health.WindowsClock.TimeServiceReading, judgement, config), Reference(judgement, config), Fight(health.TimeJumpsBack)];
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

    private static Verdict Judged(WindowsTimeService s, ClockJudgement judgement, bool manualWarns, string limit) => s switch
    {
        { IsRunning: false } when judgement.WindowsIsWrong => new(TimeServiceId, Level.Critical, s.Text, limit, $"the Windows Time service is not running and {judgement.Reason}"),
        { IsRunning: false } => new(TimeServiceId, Level.Warn, s.Text, limit, $"the Windows Time service is not running, so nothing corrects Windows' clock; on a workgroup PC Windows starts it by a trigger, so this alone is not a fault while the clock agrees — {ClockStandings.WindowsFix}"),
        { StartsAutomatically: false } when manualWarns => new(TimeServiceId, Level.Warn, s.Text, limit, "the Windows Time service runs but does not start Automatic (Windows' default is Manual): Windows or other software can stop it again — the extension's Start Windows Time sets Automatic, which does NOT restart it after a stop; only the scheduled guard (planned) does"),
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

    public static Verdict Fight(Reading<int> jumpsBack)
    {
        var limit = string.Create(CultureInfo.InvariantCulture, $"warn > {TimeJumpsBackWarnPer4h:0} in the last 4 h of this boot (thresholds.timeJumpsBackWarnPer4h)");
        return jumpsBack switch
        {
            Reading<int>.Available { Value: var n } when n > TimeJumpsBackWarnPer4h => new(
                FightId,
                Level.Warn,
                string.Create(CultureInfo.InvariantCulture, $"{n} in 4 h"),
                limit,
                string.Create(CultureInfo.InvariantCulture, $"the distro's clock was set backwards {n} times in 4 h: two time-keepers disagree (Hyper-V's time sync sets the host's clock, timesyncd sets NTP's) — clock.reference names the wrong one; journald rotates its file at each jump, which is why the journal history is short")),
            Reading<int>.Available { Value: var n } => new(FightId, Level.Ok, string.Create(CultureInfo.InvariantCulture, $"{n} in 4 h"), limit, "journald's \"Time jumped backwards\" in the last 4 h of this boot (monotonic clock)"),
            var unknown => new(FightId, Level.Unknown, string.Empty, limit, unknown.ReasonOrEmpty),
        };
    }
}
