using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Processes;
using WslCare.Core.Systemd;

namespace WslCare.Core.Health;

/// <summary>The wall clock and the monotonic clock read at one instant — the start of a clock measurement, so its end can
/// tell whether the wall clock JUMPED while it ran (PLAN_windows_time_guard.md D2).</summary>
public sealed record ClockMark(DateTimeOffset Wall, long Timestamp)
{
    public static ClockMark Now(TimeProvider clock) => new(clock.GetUtcNow(), clock.GetTimestamp());
}

/// <summary>
/// The independent clock reference (PLAN_windows_time_guard.md D2) — ONE function, for the full run and A16's preview:
/// the HTTP <c>Date</c> of <c>clock.referenceUrl</c>, else timesyncd when it is trustworthy NOW (synchronised, its last NTP
/// sample within the tolerance), else unavailable naming both reasons. A wall clock that jumped during the measurement
/// voids it: in the incident of 2026-10-08 the distro's clock moved 7 200 s about every 33 s.
/// </summary>
public static class ClockReferences
{
    /// <summary>The window of the clock-fight count: the unit of <c>thresholds.timeJumpsBackWarnPer4h</c>.</summary>
    public static readonly TimeSpan FightWindow = TimeSpan.FromHours(4);

    /// <summary>What a <c>Date</c> header loses: it is cut to the second, so the instant it names is half a second later on
    /// average.</summary>
    private static readonly TimeSpan DateTruncation = TimeSpan.FromMilliseconds(500);

    public static int ToleranceSeconds => Tuning.Current.Int(ConfigKeys.Clock.ReferenceToleranceSeconds);

    public static async Task<Reading<ClockReference>> MeasureAsync(ICommandRunner runner, TimeProvider clock, ClockMark mark, CancellationToken cancellationToken)
    {
        var http = await HttpAsync(runner, clock, cancellationToken).ConfigureAwait(false);
        var reference = http is Reading<ClockReference>.Available ? http : await TimesyncAsync(runner, clock, http.ReasonOrEmpty, cancellationToken).ConfigureAwait(false);
        return Jumped(mark, clock) is { Length: > 0 } jumped ? Reading.Missing<ClockReference>(jumped) : reference;
    }

    /// <summary>Why the measurement is void — the wall clock moved more than the tolerance away from the monotonic one since
    /// <paramref name="mark"/> — or empty.</summary>
    public static string Jumped(ClockMark mark, TimeProvider clock)
    {
        var wall = clock.GetUtcNow() - mark.Wall;
        var jump = (wall - clock.GetElapsedTime(mark.Timestamp)).TotalSeconds;
        return Math.Abs(jump) > ToleranceSeconds
            ? string.Create(CultureInfo.InvariantCulture, $"the distro's clock jumped by {jump:+0.0;-0.0} s during the measurement (two time-keepers are setting it)")
            : string.Empty;
    }

    private static async Task<Reading<ClockReference>> HttpAsync(ICommandRunner runner, TimeProvider clock, CancellationToken cancellationToken)
    {
        var url = Tuning.Current.Config.Text(ConfigKeys.Clock.ReferenceUrl);
        if (url.Length == 0)
        {
            return Reading.Missing<ClockReference>("clock.referenceUrl is empty: the HTTP reference is off");
        }

        var command = HealthCommands.ClockReference(url, HealthCommands.ReferenceSeconds);
        var before = clock.GetUtcNow();
        var outcome = await runner.RunAsync(command.ToRequest(), cancellationToken).ConfigureAwait(false);
        var after = clock.GetUtcNow();
        var started = outcome.StartedAt.ValueOr(before);
        var midpoint = started + ((after - started) / 2);
        return ToolAnswers.Read(command, outcome).Bind(ClockParsers.HttpDate)
            .Map(date => new ClockReference($"{url} (HTTP Date)", (date + DateTruncation - midpoint).TotalSeconds, midpoint));
    }

    private static async Task<Reading<ClockReference>> TimesyncAsync(ICommandRunner runner, TimeProvider clock, string httpReason, CancellationToken cancellationToken)
    {
        var sync = (await ToolAnswers.RunAsync(runner, SystemdCommands.TimeSync, cancellationToken).ConfigureAwait(false)).Bind(HealthParsers.TimeSync);
        if (sync is not Reading<TimeSync>.Available { Value.Synchronized: true })
        {
            return Reading.Missing<ClockReference>($"{httpReason}; timesyncd: {(sync is Reading<TimeSync>.Available ? "not synchronised" : sync.ReasonOrEmpty)}");
        }

        var status = (await ToolAnswers.RunAsync(runner, SystemdCommands.TimesyncStatus, cancellationToken).ConfigureAwait(false)).Bind(ClockParsers.Timesync);
        return status switch
        {
            Reading<TimesyncSample>.Available { Value: var s } when Math.Abs(s.OffsetSeconds) <= ToleranceSeconds =>
                Reading.Of(new ClockReference(string.Create(CultureInfo.InvariantCulture, $"timesyncd ({s.Server}, last NTP offset {s.OffsetSeconds:+0.000;-0.000} s)"), 0, clock.GetUtcNow())),
            Reading<TimesyncSample>.Available { Value: var s } =>
                Reading.Missing<ClockReference>(string.Create(CultureInfo.InvariantCulture, $"{httpReason}; timesyncd: its last NTP sample found the distro's clock {s.OffsetSeconds:+0.0;-0.0} s off — something else set it (Hyper-V's time sync sets the host's clock)")),
            _ => Reading.Missing<ClockReference>($"{httpReason}; timesyncd: {status.ReasonOrEmpty}"),
        };
    }

    /// <summary>journald's backward jumps in the last <see cref="FightWindow"/> of this boot, counted on the MONOTONIC clock
    /// (D4): the stamps at or after <paramref name="uptime"/> − the window.</summary>
    public static int JumpsInWindow(IReadOnlyList<string> lines, TimeSpan uptime) =>
        ClockParsers.MonotonicStamps(lines).Count(s => s >= (uptime - FightWindow).TotalSeconds);
}
