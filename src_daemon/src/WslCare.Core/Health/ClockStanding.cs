using System.Globalization;
using System.Text.Json.Serialization;

using WslCare.Core.Collectors;
using WslCare.Core.Records;

namespace WslCare.Core.Health;

/// <summary>The Windows Time service as the clock probe printed it (PLAN_windows_time_guard.md D1): its
/// <c>ServiceController.Status</c> (<c>Running</c>, <c>Stopped</c>, <c>StartPending</c>, …) and <c>StartType</c>
/// (<c>Automatic</c>, <c>Manual</c>, <c>Disabled</c>, …), as PowerShell names them. An empty member was printed empty
/// (no such service, or it could not be asked).</summary>
public sealed record WindowsTimeService(string Status, string StartType)
{
    [JsonIgnore]
    public bool IsRunning => Status == "Running";

    [JsonIgnore]
    public bool StartsAutomatically => StartType == "Automatic";

    [JsonIgnore]
    public bool IsDisabled => StartType == "Disabled";

    /// <summary>"Running, StartType Manual" — the words every sentence about it uses.</summary>
    [JsonIgnore]
    public string Text => $"{(Status.Length > 0 ? Status : "state unknown")}, StartType {(StartType.Length > 0 ? StartType : "unknown")}";
}

/// <summary>An independent clock reference (D2): where it came from, and the reference minus the distro's clock.</summary>
/// <param name="Source">"https://www.microsoft.com (HTTP Date)" or "timesyncd (ntp.ubuntu.com, last NTP offset -0.018 s)".</param>
/// <param name="ReferenceMinusWslSeconds">R: positive when the distro's clock is BEHIND the reference.</param>
public sealed record ClockReference(string Source, double ReferenceMinusWslSeconds, DateTimeOffset SampledAt);

/// <summary>Which clock is wrong — the closed verdict of D3.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClockStanding>))]
public enum ClockStanding
{
    [JsonStringEnumMemberName("unknown")]
    Unknown,

    [JsonStringEnumMemberName("agree")]
    Agree,

    [JsonStringEnumMemberName("windowsSlow")]
    WindowsSlow,

    [JsonStringEnumMemberName("windowsFast")]
    WindowsFast,

    [JsonStringEnumMemberName("wslWrong")]
    WslWrong,
}

/// <summary>The judgement of D3: the standing, the two offsets it rests on, and the sentence a person reads.</summary>
/// <param name="WindowsMinusReferenceSeconds">W − R; 0 when unknown.</param>
/// <param name="WslMinusReferenceSeconds">−R; 0 when unknown.</param>
/// <param name="DistroAlsoOff">Windows is the wrong clock AND the distro is off the reference too (Hyper-V's time sync set
/// it to the host's) — then no sentence says the distro is right.</param>
public sealed record ClockJudgement(ClockStanding Standing, double WindowsMinusReferenceSeconds, double WslMinusReferenceSeconds, bool DistroAlsoOff, string Source, string Reason)
{
    [JsonIgnore]
    public bool WindowsIsWrong => Standing is ClockStanding.WindowsSlow or ClockStanding.WindowsFast;
}

/// <summary>D3, pure: which clock is wrong. Windows is judged FIRST, so a distro Hyper-V has just dragged to the wrong host
/// time still names Windows.</summary>
public static class ClockStandings
{
    /// <summary>The one fix for a wrong Windows clock, in the words every sentence uses.</summary>
    public const string WindowsFix = "start the Windows Time service and resync: the extension's Start Windows Time (one UAC prompt), or as administrator `Start-Service w32time; w32tm /resync /force`";

    /// <param name="windows">The probe's observation: W = Windows − the distro.</param>
    /// <param name="reference">R = the reference − the distro, or why there is none.</param>
    /// <param name="toleranceSeconds">T, <c>clock.referenceToleranceSeconds</c>.</param>
    public static ClockJudgement Judge(WindowsClockSample windows, Reading<ClockReference> reference, int toleranceSeconds)
    {
        if (!windows.Measured)
        {
            return Unknown($"the Windows clock was not observed: {windows.Unavailable}");
        }

        return reference is Reading<ClockReference>.Available { Value: var r }
            ? Of(windows.OffsetSeconds, r, toleranceSeconds)
            : Unknown($"no independent clock reference: {reference.ReasonOrEmpty}");
    }

    /// <summary>
    /// The CAUSE of a clock that disagrees, in one sentence (PLAN_windows_time_guard.md D8): which clock is wrong against the
    /// reference, the Windows Time service as the probe printed it, and the fix — what the live contract fails with when the
    /// probe is off by a minute, so the release checklist says what <c>status</c> says, never only "a broken clock".
    /// </summary>
    public static string Diagnosis(WindowsClockSample windows, ClockJudgement judgement)
    {
        var service = windows.TimeService is { } s ? $"w32time: {s.Text}" : "w32time: not printed";
        return judgement.Standing switch
        {
            ClockStanding.WindowsSlow or ClockStanding.WindowsFast => Invariant($"the Windows clock disagrees with {judgement.Source} by {judgement.WindowsMinusReferenceSeconds:+0.0;-0.0} s — is the Windows Time service running? ({service}) — fix: {WindowsFix}"),
            ClockStanding.WslWrong => Invariant($"the distro's clock is {judgement.WslMinusReferenceSeconds:+0.0;-0.0} s off {judgement.Source} while Windows agrees ({service}) — A16 or timesyncd corrects the distro"),
            ClockStanding.Agree => Invariant($"Windows and the distro disagree by {windows.OffsetSeconds:+0.0;-0.0} s, yet both agree with {judgement.Source} — the measurement is suspect ({service})"),
            _ => Invariant($"the clocks disagree by {windows.OffsetSeconds:+0.0;-0.0} s and no reference can say which is wrong: {judgement.Reason} — is the Windows Time service running? ({service})"),
        };
    }

    private static ClockJudgement Of(double w, ClockReference reference, int tolerance)
    {
        var windowsOff = w - reference.ReferenceMinusWslSeconds;
        var wslOff = -reference.ReferenceMinusWslSeconds;
        var distroOff = Math.Abs(wslOff) > tolerance;
        var standing = StandingOf(windowsOff, distroOff, tolerance);
        var alsoOff = distroOff && standing != ClockStanding.WslWrong;
        return new(standing, windowsOff, wslOff, alsoOff, reference.Source, Sentence(standing, windowsOff, wslOff, alsoOff, reference.Source, tolerance));
    }

    private static ClockStanding StandingOf(double windowsOff, bool distroOff, int tolerance) => windowsOff switch
    {
        _ when windowsOff < -tolerance => ClockStanding.WindowsSlow,
        _ when windowsOff > tolerance => ClockStanding.WindowsFast,
        _ => distroOff ? ClockStanding.WslWrong : ClockStanding.Agree,
    };

    private static string Sentence(ClockStanding standing, double windowsOff, double wslOff, bool alsoOff, string source, int tolerance) => standing switch
    {
        ClockStanding.WindowsSlow or ClockStanding.WindowsFast => Invariant(
            $"the Windows clock is {Math.Abs(windowsOff):0.0} s {(standing == ClockStanding.WindowsSlow ? "slow" : "fast")} against {source}{(alsoOff ? Invariant($"; the distro's clock is off too ({wslOff:+0.0;-0.0} s): Hyper-V's time sync set it to the host's") : string.Empty)} — {WindowsFix}"),
        ClockStanding.WslWrong => Invariant($"the distro's clock is {wslOff:+0.0;-0.0} s off {source} while Windows agrees with it (within {tolerance} s) — A16 or timesyncd corrects the distro"),
        _ => Invariant($"Windows ({windowsOff:+0.0;-0.0} s) and the distro ({wslOff:+0.0;-0.0} s) agree with {source} within {tolerance} s"),
    };

    private static ClockJudgement Unknown(string reason) => new(ClockStanding.Unknown, 0, 0, false, string.Empty, reason);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
