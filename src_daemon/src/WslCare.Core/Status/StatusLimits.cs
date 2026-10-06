using WslCare.Core.Config;

namespace WslCare.Core.Status;

/// <summary>
/// E7.S2c: the daemon values the extension MIRRORS instead of copying (the owner, 2026-10-05; PR #12's
/// <c>shared/daemonLimits.ts</c>), published in <c>status --json</c> as <c>limits</c> — additive, schema version 1. Each field is
/// the value IN FORCE (the effective configuration this answer loaded), a whole number, named by <see cref="Fields"/> — the list
/// <c>contracts/status-limits.json</c> is generated from, so the extension's reader and this writer cannot drift apart.
/// </summary>
/// <remarks>What is here and why: the two the extension asked for (the history it may ask about, the clock skew a request may
/// carry), and every other daemon value a host decision rests on whose machine range reaches ABOVE its default — a copy of
/// the default would then be too small. A ceiling whose range maximum IS its default (every Docker and <c>systemctl show</c>
/// ceiling) can only be lowered, so the extension's copy of the default stays a safe upper bound and is not published.</remarks>
/// <param name="HistoryRetentionDays"><c>runs.historyRetentionDays</c>: the days of history the daemon keeps.</param>
/// <param name="RequestFutureSkewSeconds"><c>requests.futureSkewSeconds</c>: how far ahead of the clock a request may be.</param>
/// <param name="RequestGraceSeconds"><c>requests.graceSeconds</c>: how long a request waits for its unit before it is swept —
/// a host waits past it before it resolves a confirm from the history.</param>
/// <param name="MaxShownNames"><c>act.maxShownNames</c>: the most names one shown list may carry.</param>
/// <param name="UnitStopSeconds"><c>systemd.unitStopTimeoutSeconds</c>: the ceiling of <c>act --stop</c>'s <c>systemctl stop</c>.</param>
/// <param name="DrainGraceMilliseconds"><c>commands.drainGraceMilliseconds</c>: what a killed command adds, twice, to its ceiling.</param>
public sealed record StatusLimits(
    int HistoryRetentionDays,
    int RequestFutureSkewSeconds,
    int RequestGraceSeconds,
    int MaxShownNames,
    int UnitStopSeconds,
    int DrainGraceMilliseconds)
{
    /// <summary>Every field: its wire name, the key it publishes, its unit — in wire order.</summary>
    public static IReadOnlyList<(string Name, ConfigKey.IntKey Key, string Unit)> Fields { get; } =
    [
        ("historyRetentionDays", ConfigKeys.Runs.HistoryRetentionDays, "days"),
        ("requestFutureSkewSeconds", ConfigKeys.Requests.FutureSkewSeconds, "seconds"),
        ("requestGraceSeconds", ConfigKeys.Requests.GraceSeconds, "seconds"),
        ("maxShownNames", ConfigKeys.Act.MaxShownNames, "names"),
        ("unitStopSeconds", ConfigKeys.Systemd.UnitStopTimeoutSeconds, "seconds"),
        ("drainGraceMilliseconds", ConfigKeys.Commands.DrainGraceMilliseconds, "milliseconds"),
    ];

    /// <summary>The values in force under <paramref name="config"/>.</summary>
    public static StatusLimits From(EffectiveConfig config) => new(
        config.Int(ConfigKeys.Runs.HistoryRetentionDays),
        config.Int(ConfigKeys.Requests.FutureSkewSeconds),
        config.Int(ConfigKeys.Requests.GraceSeconds),
        config.Int(ConfigKeys.Act.MaxShownNames),
        config.Int(ConfigKeys.Systemd.UnitStopTimeoutSeconds),
        config.Int(ConfigKeys.Commands.DrainGraceMilliseconds));
}
