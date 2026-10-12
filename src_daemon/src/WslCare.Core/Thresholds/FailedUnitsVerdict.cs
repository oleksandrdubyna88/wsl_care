using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Health;

namespace WslCare.Core.Thresholds;

/// <summary>
/// <c>systemd.failedUnits</c> (plan §4), PURE: it warns at one or more failed units that are NOT in
/// <c>systemd.ignoredFailedUnits</c> (the coordinator, 2026-10-12, after the first live full run: WSL's <c>getty@tty1.service</c>
/// hits its start limit on every boot). An ignored unit is still named in the reason, so nothing is hidden; the run detail keeps
/// every failed unit.
/// </summary>
public static class FailedUnitsVerdict
{
    public const string Id = "systemd.failedUnits";

    private const string Limit = "warn at 1 or more failed units not in systemd.ignoredFailedUnits";

    public static Verdict Evaluate(Reading<IReadOnlyList<FailedUnit>> units, EffectiveConfig config) => units switch
    {
        Reading<IReadOnlyList<FailedUnit>>.Available { Value: var list } => Judged(list, config.TextList(ConfigKeys.Systemd.IgnoredFailedUnits)),
        var unread => new(Id, Level.Unknown, string.Empty, Limit, unread.ReasonOrEmpty),
    };

    private static Verdict Judged(IReadOnlyList<FailedUnit> units, IReadOnlyList<string> ignore)
    {
        var real = units.Where(u => !ignore.Contains(u.Unit, StringComparer.Ordinal)).Select(u => u.Unit).ToList();
        var ignored = units.Where(u => ignore.Contains(u.Unit, StringComparer.Ordinal)).Select(u => u.Unit).ToList();
        return new(Id, real.Count > 0 ? Level.Warn : Level.Ok, real.Count.ToString(CultureInfo.InvariantCulture), Limit, Reason(real, ignored));
    }

    private static string Reason(IReadOnlyList<string> real, IReadOnlyList<string> ignored)
    {
        var failed = real.Count > 0 ? $"failed units: {string.Join(", ", real)}" : "no failed unit that counts";
        return ignored.Count > 0 ? $"{failed}; ignored (systemd.ignoredFailedUnits): {string.Join(", ", ignored)}" : failed;
    }
}
