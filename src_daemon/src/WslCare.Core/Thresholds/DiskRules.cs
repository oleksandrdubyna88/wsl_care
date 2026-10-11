using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Health;

namespace WslCare.Core.Thresholds;

/// <summary>
/// E15.S2's three verdicts over one full run's <see cref="DiskFacts"/> (PLAN_disk_health.md, design §4), PURE — spread into
/// <see cref="ThresholdRules"/>' full-run list, so <c>status</c> carries them with the run and their age:
/// <list type="bullet">
/// <item><c>disk.vhdxGap</c> — a virtual disk file larger than its guest uses by more than <c>disk.vhdxGapWarnGib</c>; a distro
/// file that is not sparse gets the sparse advice as TEXT (wsl-care never runs it); Docker's gap says it is Docker's estimate;</item>
/// <item><c>disk.vhdxGrowth</c> — a file that grew faster than <c>disk.vhdxGrowthWarnGibPerDay</c>, between two measured sizes;</item>
/// <item><c>disk.hostFree</c> — a hosting volume with less free than <c>disk.hostFreeWarnGib</c>, or than the KNOWN possible growth of
/// its files; while any file's possible growth is unknown it is <c>unknown</c>, never <c>ok</c> (plan round, 2026-10-11).</item>
/// </list>
/// One verdict per id over every disk / volume: the level is the worst, the value names each.
/// </summary>
public static class DiskRules
{
    public const string GapId = "disk.vhdxGap";

    public const string GrowthId = "disk.vhdxGrowth";

    public const string HostFreeId = "disk.hostFree";

    /// <summary>Bytes in a GiB: Windows reports the file sizes in binary units.</summary>
    private const double Gib = 1024d * 1024 * 1024;

    public static IReadOnlyList<Verdict> Evaluate(Reading<DiskFacts> disk, EffectiveConfig config) =>
    [
        Gap(disk, config.Int(ConfigKeys.Disk.VhdxGapWarnGib)),
        Growth(disk, config.Int(ConfigKeys.Disk.VhdxGrowthWarnGibPerDay)),
        HostFree(disk, config.Int(ConfigKeys.Disk.HostFreeWarnGib)),
    ];

    // ---------- disk.vhdxGap ----------

    private static Verdict Gap(Reading<DiskFacts> disk, int warnGib)
    {
        var limit = $"warn > {warnGib} GiB of a virtual disk file its guest does not use (disk.vhdxGapWarnGib)";
        var mapped = disk.Map(f => f.Disks.Where(d => d.GapBytes.IsAvailable).ToList());
        return mapped switch
        {
            Reading<List<VirtualDiskFacts>>.Available { Value.Count: > 0 } m => JudgedGap(m.Value, warnGib, limit),
            Reading<List<VirtualDiskFacts>>.Available => Unknown(GapId, limit, $"no virtual disk is mapped to its guest — {Grouped(disk.Map(f => f.Disks).ValueOr([]), d => d.GapBytes.ReasonOrEmpty)}"),
            var unread => Unknown(GapId, limit, unread.ReasonOrEmpty),
        };
    }

    private static Verdict JudgedGap(IReadOnlyList<VirtualDiskFacts> mapped, int warnGib, string limit)
    {
        var over = mapped.Where(d => d.GapBytes.ValueOr(0) > warnGib * Gib).ToList();
        var value = string.Join("; ", mapped.Select(d => Invariant($"{d.Name}: {d.SizeBytes.ValueOr(0) / Gib:0.0} − {d.GuestUsedBytes.ValueOr(0) / Gib:0.0} = {d.GapBytes.ValueOr(0) / Gib:0.0} GiB")));
        return over.Count > 0
            ? new(GapId, Level.Warn, value, limit, $"{string.Join(", ", over.Select(d => d.Name))}: the file holds more than its guest uses — {string.Join("; ", over.Select(GapAdvice))}")
            : new(GapId, Level.Ok, value, limit, $"every mapped virtual disk is within the limit{DockerLabel(mapped)}");
    }

    /// <summary>What to do about one disk's gap — TEXT: the sparse switch for a distro file that is not sparse, Docker's label for
    /// Docker's data disk.</summary>
    private static string GapAdvice(VirtualDiskFacts disk) => disk switch
    {
        { MappedBy: MappedBy.DockerDf } => $"{disk.Name}: {VirtualDisks.DockerNote}",
        { Sparse: Reading<bool>.Available { Value: false } } => $"{disk.Name} is not sparse: wsl --manage {disk.Name} --set-sparse true lets Windows reclaim the space freed inside it — wsl-care never runs it",
        _ => $"{disk.Name}: freed blocks reach the file only through discard or fstrim (disk.discard)",
    };

    private static string DockerLabel(IReadOnlyList<VirtualDiskFacts> mapped) =>
        mapped.Any(d => d.MappedBy == MappedBy.DockerDf) ? $" ({VirtualDisks.DockerNote})" : string.Empty;

    // ---------- disk.vhdxGrowth ----------

    private static Verdict Growth(Reading<DiskFacts> disk, int warnGibPerDay)
    {
        var limit = $"warn > {warnGibPerDay} GiB/day between two measured sizes (disk.vhdxGrowthWarnGibPerDay)";
        var known = disk.Map(f => f.Disks.Where(d => d.GrowthBytesPerDay.IsAvailable).ToList());
        return known switch
        {
            Reading<List<VirtualDiskFacts>>.Available { Value.Count: > 0 } k => JudgedGrowth(k.Value, warnGibPerDay, limit),
            Reading<List<VirtualDiskFacts>>.Available => Unknown(GrowthId, limit, Grouped(disk.Map(f => f.Disks).ValueOr([]), d => d.GrowthBytesPerDay.ReasonOrEmpty)),
            var unread => Unknown(GrowthId, limit, unread.ReasonOrEmpty),
        };
    }

    private static Verdict JudgedGrowth(IReadOnlyList<VirtualDiskFacts> known, int warnGibPerDay, string limit)
    {
        var over = known.Where(d => d.GrowthBytesPerDay.ValueOr(0) > warnGibPerDay * Gib).ToList();
        var value = string.Join("; ", known.Select(d => Invariant($"{d.Name} {d.GrowthBytesPerDay.ValueOr(0) / Gib:+0.0;-0.0} GiB/day")));
        return over.Count > 0
            ? new(GrowthId, Level.Warn, value, limit, $"{string.Join(", ", over.Select(d => d.Name))} grew faster than the limit since the previous full run")
            : new(GrowthId, Level.Ok, value, limit, "the growth since the previous full run, from two measured sizes of each file");
    }

    // ---------- disk.hostFree ----------

    private static Verdict HostFree(Reading<DiskFacts> disk, int floorGib)
    {
        var limit = $"warn when a volume holding virtual disks has less than {floorGib} GiB free (disk.hostFreeWarnGib) or less than its files can still grow; unknown while any file's possible growth is unknown";
        return disk switch
        {
            Reading<DiskFacts>.Available { Value.Volumes.Count: > 0 } d => JudgedHosts(d.Value, floorGib, limit),
            Reading<DiskFacts>.Available => Unknown(HostFreeId, limit, "no virtual disk file is on a lettered volume"),
            var unread => Unknown(HostFreeId, limit, unread.ReasonOrEmpty),
        };
    }

    private static Verdict JudgedHosts(DiskFacts facts, int floorGib, string limit)
    {
        var judged = facts.Volumes.Select(v => Host(v, facts, floorGib)).ToList();
        var value = string.Join("; ", facts.Volumes.Select(v => v.FreeBytes is Reading<long>.Available { Value: var free } ? Invariant($"{v.Letter}: {free / Gib:0.0} GiB free") : $"{v.Letter}: free not read"));
        return new(HostFreeId, Worst(judged.Select(j => j.Level)), value, limit, string.Join("; ", judged.Select(j => j.Reason)));
    }

    private sealed record Judged(Level Level, string Reason);

    private static Judged Host(HostingVolumeFacts volume, DiskFacts facts, int floorGib) => volume.FreeBytes switch
    {
        Reading<long>.Available { Value: var free } => HostWithFree(volume, free, facts, floorGib),
        var unread => new(Level.Unknown, $"{volume.Letter}: the free space was not read: {unread.ReasonOrEmpty}"),
    };

    private static Judged HostWithFree(HostingVolumeFacts volume, long free, DiskFacts facts, int floorGib)
    {
        var known = volume.KnownPossibleGrowthBytes / Gib;
        return free switch
        {
            _ when free < floorGib * Gib => new(Level.Warn, Invariant($"{volume.Letter}: {free / Gib:0.0} GiB free is below the floor of {floorGib} GiB")),
            _ when free < volume.KnownPossibleGrowthBytes => new(Level.Warn, Invariant($"{volume.Letter}: {free / Gib:0.0} GiB free is less than the {known:0.0} GiB its virtual disks can still grow")),
            _ when volume.UnknownGrowth.Count > 0 => new(Level.Unknown, Invariant($"{volume.Letter}: the possible growth of {Files(volume.UnknownGrowth.Count)} is not known ({Grouped(Unknowns(volume, facts), d => d.PossibleGrowthBytes.ReasonOrEmpty)}); {free / Gib:0.0} GiB free is above the floor of {floorGib} GiB and the known growth of {known:0.0} GiB")),
            _ => new(Level.Ok, Invariant($"{volume.Letter}: {free / Gib:0.0} GiB free is above the floor of {floorGib} GiB and the {known:0.0} GiB its virtual disks can still grow")),
        };
    }

    private static IReadOnlyList<VirtualDiskFacts> Unknowns(HostingVolumeFacts volume, DiskFacts facts) =>
        [.. facts.Disks.Where(d => volume.UnknownGrowth.Contains(d.Name, StringComparer.Ordinal))];

    private static string Files(int count) => count == 1 ? "1 file" : Invariant($"{count} files");

    /// <summary>Warn above unknown above ok: a breach is a breach whatever else is unknown, and an unknown is never an ok.</summary>
    private static Level Worst(IEnumerable<Level> levels) => levels.Aggregate(Level.Ok, (worst, level) => Rank(level) > Rank(worst) ? level : worst);

    private static int Rank(Level level) => level switch
    {
        Level.Ok => 0,
        Level.Unknown => 1,
        _ => 2,
    };

    // ---------- shared ----------

    /// <summary>"Ubuntu, docker-desktop: why; docker-data: why" — the disks grouped by the reason a figure of theirs is not known.</summary>
    private static string Grouped(IReadOnlyList<VirtualDiskFacts> disks, Func<VirtualDiskFacts, string> reason) =>
        string.Join("; ", disks.GroupBy(reason, StringComparer.Ordinal).Select(g => $"{string.Join(", ", g.Select(d => d.Name))}: {g.Key}"));

    private static Verdict Unknown(string id, string limit, string reason) => new(id, Level.Unknown, string.Empty, limit, reason);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
