using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Health;

namespace WslCare.Core.Thresholds;

/// <summary>
/// E19.S1's verdict <c>disk.volumeFree</c> over EVERY lettered Windows volume of one full run's Windows disk sample (PLAN_disk_hygiene_windows.md
/// §3.2), PURE — spread into <see cref="ThresholdRules"/>' full-run list right after <see cref="DiskRules"/>, so <c>status</c> carries it with
/// the run and its age. Per volume the levels are an OR, critical first: <c>critical</c> when the free space is under the critical GiB floor
/// or the free share under the critical percent, <c>warn</c> under the warn pair. The GiB floors apply only to a volume of at least
/// <c>disk.volumeFreeAbsoluteMinSizeGib</c>; a smaller one is judged by percent alone. A volume under <c>disk.volumeFreeMinSizeGib</c> or in
/// <c>disk.volumeFreeIgnoreLetters</c> is not judged. Across volumes <c>critical &gt; warn &gt; unknown &gt; ok</c>: an unread figure is
/// <c>unknown</c>, never <c>ok</c>, and never hides a breach. Not <see cref="DiskRules"/>: that rule sees only the volumes that HOST virtual
/// disks, and its <c>Rank</c> has no <c>critical</c>.
/// </summary>
public static class VolumeFreeRules
{
    public const string Id = "disk.volumeFree";

    /// <summary>The GiB floors and the percent floors judged the volume.</summary>
    public const string JudgedByGibOrPercent = "gib-or-percent";

    /// <summary>The percent floors alone judged the volume (it is under the absolute-size floor).</summary>
    public const string JudgedByPercent = "percent";

    /// <summary>What every <c>judgedBy</c> of a volume that did not take part in the level starts with.</summary>
    public const string NotJudgedPrefix = "not judged: ";

    /// <summary>Bytes in a GiB: Windows reports the volume sizes in binary units.</summary>
    private const double Gib = 1024d * 1024 * 1024;

    public static VolumeFreeAssessment Evaluate(Reading<IReadOnlyList<WindowsVolume>> volumes, VolumeFreeLimits limits)
    {
        var limit = LimitText(limits);
        return volumes switch
        {
            Reading<IReadOnlyList<WindowsVolume>>.Available { Value: var list } => Assess([.. list.Select(v => Judge(v, limits))], limit),
            var unread => new(Unknown(limit, unread.ReasonOrEmpty), []),
        };
    }

    /// <summary>The full run's own sample, judged under the configuration in force — through <c>health.WindowsDisk.Facts.Volumes</c>, so the
    /// answer does not depend on the Distros map being readable.</summary>
    public static VolumeFreeAssessment From(HealthSample health, EffectiveConfig config) =>
        Evaluate(health.WindowsDisk.Facts.Bind(f => f.Volumes), VolumeFreeLimits.From(config));

    // ---------- per volume ----------

    private static VolumeJudgement Judge(WindowsVolume volume, VolumeFreeLimits limits) =>
        limits.IgnoreLetters.Contains(volume.Letter, StringComparer.OrdinalIgnoreCase)
            ? Excluded(volume, "its letter is in disk.volumeFreeIgnoreLetters")
            : JudgeSized(volume, limits);

    private static VolumeJudgement JudgeSized(WindowsVolume volume, VolumeFreeLimits limits) => volume.SizeBytes switch
    {
        Reading<long>.Available { Value: var size } => JudgeRead(volume, size, limits),
        var unread => Unread(volume, $"its size was not read: {unread.ReasonOrEmpty}"),
    };

    private static VolumeJudgement JudgeRead(WindowsVolume volume, long size, VolumeFreeLimits limits) =>
        (TooSmall(size, limits), volume.FreeBytes) switch
        {
            (true, _) => Excluded(volume, Invariant($"smaller than {limits.MinSizeGib} GiB (disk.volumeFreeMinSizeGib)")),
            (_, Reading<long>.Available { Value: var free }) => Levelled(volume, size, free, limits),
            (_, var unread) => Unread(volume, $"its free space was not read: {unread.ReasonOrEmpty}"),
        };

    private static bool TooSmall(long size, VolumeFreeLimits limits) => size < limits.MinSizeGib * Gib;

    private static VolumeJudgement Levelled(WindowsVolume volume, long size, long free, VolumeFreeLimits limits)
    {
        var gibFloors = size >= limits.AbsoluteMinSizeGib * Gib;
        var level = LevelOf(free, free * 100d / size, gibFloors, limits);
        return new(volume.Letter, volume.SizeBytes, volume.FreeBytes, level, gibFloors ? JudgedByGibOrPercent : JudgedByPercent, true);
    }

    private static Level LevelOf(long free, double percent, bool gibFloors, VolumeFreeLimits limits) =>
        Below(free, percent, gibFloors, limits.CriticalGib, limits.CriticalPercent) ? Level.Critical
        : Below(free, percent, gibFloors, limits.WarnGib, limits.WarnPercent) ? Level.Warn
        : Level.Ok;

    /// <summary>A floor is a strict limit: a volume exactly on it has not breached it.</summary>
    private static bool Below(long free, double percent, bool gibFloors, int gib, int percentFloor) =>
        (gibFloors && free < gib * Gib) || percent < percentFloor;

    /// <summary>Listed, but not part of the level: too small, or an ignored letter.</summary>
    private static VolumeJudgement Excluded(WindowsVolume volume, string why) =>
        new(volume.Letter, volume.SizeBytes, volume.FreeBytes, Level.Unknown, NotJudgedPrefix + why, false);

    /// <summary>A figure that decides whether and how the volume is judged was not read: the verdict is <c>unknown</c> at best.</summary>
    private static VolumeJudgement Unread(WindowsVolume volume, string why) =>
        new(volume.Letter, volume.SizeBytes, volume.FreeBytes, Level.Unknown, NotJudgedPrefix + why, true);

    // ---------- across volumes ----------

    private static VolumeFreeAssessment Assess(IReadOnlyList<VolumeJudgement> all, string limit)
    {
        var counted = all.Where(v => v.InVerdict).OrderByDescending(v => Rank(v.Level)).ThenBy(v => v.FreePercent.ValueOr(double.MaxValue)).ThenBy(v => v.Letter, StringComparer.Ordinal).ToList();
        var reason = string.Join("; ", all.Select(Explain));
        return counted.Count == 0
            ? new(Unknown(limit, NothingJudged(all.Count, reason)), all)
            : new(new Verdict(Id, counted[0].Level, string.Join("; ", counted.Select(Describe)), limit, reason), all);
    }

    private static string NothingJudged(int volumes, string reason) =>
        volumes == 0 ? "the sample lists no lettered volume" : $"no lettered volume is judged ({reason})";

    /// <summary>Critical above warn above unknown above ok: a breach is a breach whatever else is unread, and an unknown is never an ok. A
    /// rank of its own — <see cref="DiskRules"/> ranks only ok, unknown and "else", so a critical would read as a warn there.</summary>
    private static int Rank(Level level) => level switch
    {
        Level.Critical => 3,
        Level.Warn => 2,
        Level.Unknown => 1,
        _ => 0,
    };

    // ---------- the words ----------

    private static string LimitText(VolumeFreeLimits l) =>
        Invariant($"warn < {l.WarnGib} GiB or < {l.WarnPercent} %, critical < {l.CriticalGib} GiB or < {l.CriticalPercent} % (GiB floors only on volumes of {l.AbsoluteMinSizeGib} GiB or more; volumes under {l.MinSizeGib} GiB not judged)");

    /// <summary>"D: 4.1 GiB free (2.1 %) — critical"; a figure not read says so instead of showing 0.</summary>
    private static string Describe(VolumeJudgement v) => (v.FreeBytes, v.FreePercent) switch
    {
        (Reading<long>.Available { Value: var free }, Reading<double>.Available { Value: var percent }) =>
            Invariant($"{v.Letter}: {free / Gib:0.0} GiB free ({percent:0.0} %) — {Spelling(v.Level)}"),
        (Reading<long>.Available, _) => $"{v.Letter}: size not read — {Spelling(v.Level)}",
        _ => $"{v.Letter}: free not read — {Spelling(v.Level)}",
    };

    /// <summary>One volume's part of the reason: which rule judged it and, for a breach, which floor — or why it was not judged.</summary>
    private static string Explain(VolumeJudgement v) => v.JudgedBy switch
    {
        JudgedByGibOrPercent => $"{v.Letter}: judged by GiB or percent{Breach(v.Level)}",
        JudgedByPercent => $"{v.Letter}: judged by percent alone{Breach(v.Level)}",
        _ => $"{v.Letter}: {v.JudgedBy}",
    };

    private static string Breach(Level level) => level is Level.Warn or Level.Critical ? $", below the {Spelling(level)} floor" : string.Empty;

    private static string Spelling(Level level) => level.ToString().ToLowerInvariant();

    private static Verdict Unknown(string limit, string reason) => new(Id, Level.Unknown, string.Empty, limit, reason);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
