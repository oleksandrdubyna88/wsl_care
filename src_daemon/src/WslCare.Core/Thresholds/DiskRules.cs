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
            Reading<List<VirtualDiskFacts>>.Available { Value.Count: > 0 } m => JudgedGap(m.Value, UnjudgedGaps(disk), warnGib, limit),
            Reading<List<VirtualDiskFacts>>.Available => Unknown(GapId, limit, $"no virtual disk is mapped to its guest — {Grouped(disk.Map(f => f.Disks).ValueOr([]), d => d.GapBytes.ReasonOrEmpty)}"),
            var unread => Unknown(GapId, limit, unread.ReasonOrEmpty),
        };
    }

    /// <summary>What keeps the gap from being judged in full, each as a sentence: a disk mapped to its guest whose gap could not be
    /// read (code round, 2026-10-11), and THIS distro's own disk when none is mapped by mount — its name not known, or matching no
    /// file (own review: Docker's estimate alone must not read <c>ok</c>). A disk mapped <see cref="MappedBy.None"/> because it is
    /// ANOTHER distro is outside this verdict by design.</summary>
    private static IReadOnlyList<string> UnjudgedGaps(Reading<DiskFacts> disk) =>
        disk.Map(f => (IReadOnlyList<string>)[
            .. f.Disks.Where(d => d.MappedBy != MappedBy.None && !d.GapBytes.IsAvailable).Select(d => $"{d.Name}: {d.GapBytes.ReasonOrEmpty}"),
            .. OwnDiskNotJudged(f)]).ValueOr([]);

    private static IEnumerable<string> OwnDiskNotJudged(DiskFacts facts) =>
        facts.Disks.Any(d => d.MappedBy == MappedBy.Mount) ? [] : [$"this distro's own disk is not judged: {OwnDiskReason(facts.DistroName)}"];

    private static string OwnDiskReason(Reading<string> name) =>
        name is Reading<string>.Available { Value: var n } ? $"its name, {n}, matches no virtual disk in the Lxss map" : name.ReasonOrEmpty;

    /// <summary>A known breach warns; otherwise anything not judged makes it <c>unknown</c>, never <c>ok</c>.</summary>
    private static Verdict JudgedGap(IReadOnlyList<VirtualDiskFacts> mapped, IReadOnlyList<string> unjudged, int warnGib, string limit)
    {
        var over = mapped.Where(d => d.GapBytes.ValueOr(0) > warnGib * Gib).ToList();
        var value = string.Join("; ", mapped.Select(d => Invariant($"{d.Name}: {d.SizeBytes.ValueOr(0) / Gib:0.0} − {d.GuestUsedBytes.ValueOr(0) / Gib:0.0} = {d.GapBytes.ValueOr(0) / Gib:0.0} GiB")));
        return (over.Count, unjudged.Count) switch
        {
            ( > 0, _) => new(GapId, Level.Warn, value, limit, $"{string.Join(", ", over.Select(d => d.Name))}: the file holds more than its guest uses — {string.Join("; ", over.Select(GapAdvice))}"),
            (_, > 0) => new(GapId, Level.Unknown, value, limit, $"not every gap could be judged ({string.Join("; ", unjudged)}); every gap that was read is within the limit{DockerLabel(mapped)}"),
            _ => new(GapId, Level.Ok, value, limit, $"every mapped virtual disk is within the limit{DockerLabel(mapped)}"),
        };
    }

    private static string Names(IReadOnlyList<VirtualDiskFacts> disks) => string.Join(", ", disks.Select(d => d.Name));

    /// <summary>What to do about one disk's gap — TEXT only: for a distro file that is not sparse, compaction with WSL shut down
    /// (PLAN_wsl_care_daemon.md 0.3) and NEVER the sparse switch — WSL disabled sparse VHD after data-corruption reports and a
    /// sparse VHDX cannot be compacted (research/2026-10-02_competitor_survey.md; the never-list's <c>sparse-vhd</c> rule; the
    /// E15.S2 advice that named <c>--set-sparse</c> was a defect, fixed 2026-10-11); Docker's label for Docker's data disk.</summary>
    private static string GapAdvice(VirtualDiskFacts disk) => disk switch
    {
        { MappedBy: MappedBy.DockerDf } => $"{disk.Name}: {VirtualDisks.DockerNote}",
        { Sparse: Reading<bool>.Available { Value: false } } => CompactionAdvice(disk),
        _ => $"{disk.Name}: freed blocks reach the file only through discard or fstrim (disk.discard)",
    };

    /// <summary>The compaction advice for ONE file, usable as written (plan round, 2026-10-11): its own path, the prerequisite (WSL
    /// stopped, an administrator), and both ways — <c>Optimize-VHD</c> (needs the Hyper-V module) or DiskPart's select, attach, compact
    /// and detach. TEXT only: wsl-care never compacts and never elevates.</summary>
    private static string CompactionAdvice(VirtualDiskFacts disk) =>
        $"{disk.Name}: the space freed inside the file comes back to Windows only when the file is compacted with WSL stopped — run wsl --shutdown, then as an administrator either Optimize-VHD -Path \"{disk.Path}\" -Mode Full (needs the Hyper-V module) or, in diskpart: select vdisk file=\"{disk.Path}\", attach vdisk readonly, compact vdisk, detach vdisk — wsl-care never compacts; sparse VHD is not advised: WSL disabled it after data-corruption reports, and a sparse VHDX cannot be compacted";

    private static string DockerLabel(IReadOnlyList<VirtualDiskFacts> mapped) =>
        mapped.Any(d => d.MappedBy == MappedBy.DockerDf) ? $" ({VirtualDisks.DockerNote})" : string.Empty;

    // ---------- disk.vhdxGrowth ----------

    private static Verdict Growth(Reading<DiskFacts> disk, int warnGibPerDay)
    {
        var limit = $"warn > {warnGibPerDay} GiB/day between two measured sizes (disk.vhdxGrowthWarnGibPerDay)";
        var known = disk.Map(f => f.Disks.Where(d => d.GrowthBytesPerDay.IsAvailable).ToList());
        return known switch
        {
            Reading<List<VirtualDiskFacts>>.Available { Value.Count: > 0 } k => JudgedGrowth(k.Value, disk.Map(f => (IReadOnlyList<VirtualDiskFacts>)[.. f.Disks.Where(d => !d.GrowthBytesPerDay.IsAvailable)]).ValueOr([]), warnGibPerDay, limit),
            Reading<List<VirtualDiskFacts>>.Available => Unknown(GrowthId, limit, Grouped(disk.Map(f => f.Disks).ValueOr([]), d => d.GrowthBytesPerDay.ReasonOrEmpty)),
            var unread => Unknown(GrowthId, limit, unread.ReasonOrEmpty),
        };
    }

    /// <summary>A known breach warns; otherwise a file with no growth figure (newly seen, moved, too close) makes it <c>unknown</c>,
    /// never <c>ok</c> (code round, 2026-10-11).</summary>
    private static Verdict JudgedGrowth(IReadOnlyList<VirtualDiskFacts> known, IReadOnlyList<VirtualDiskFacts> unknown, int warnGibPerDay, string limit)
    {
        var over = known.Where(d => d.GrowthBytesPerDay.ValueOr(0) > warnGibPerDay * Gib).ToList();
        var value = string.Join("; ", known.Select(d => Invariant($"{d.Name} {d.GrowthBytesPerDay.ValueOr(0) / Gib:+0.0;-0.0} GiB/day")));
        return (over.Count, unknown.Count) switch
        {
            ( > 0, _) => new(GrowthId, Level.Warn, value, limit, $"{Names(over)} grew faster than the limit since the previous full run"),
            (_, > 0) => new(GrowthId, Level.Unknown, value, limit, $"no growth yet for {Names(unknown)} ({Grouped(unknown, d => d.GrowthBytesPerDay.ReasonOrEmpty)}); every growth that was measured is within the limit"),
            _ => new(GrowthId, Level.Ok, value, limit, "the growth since the previous full run, from two measured sizes of each file"),
        };
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
        // A tuple switch, not three `when` guards: complexity ≤ 4 (own review, 2026-10-11).
        var belowFloor = free < floorGib * Gib;
        var belowKnownGrowth = free < volume.KnownPossibleGrowthBytes;
        var someGrowthUnknown = volume.UnknownGrowth.Count > 0;
        return (belowFloor, belowKnownGrowth, someGrowthUnknown) switch
        {
            (true, _, _) => new(Level.Warn, Invariant($"{volume.Letter}: {free / Gib:0.0} GiB free is below the floor of {floorGib} GiB")),
            (_, true, _) => new(Level.Warn, Invariant($"{volume.Letter}: {free / Gib:0.0} GiB free is less than the {known:0.0} GiB its virtual disks can still grow")),
            (_, _, true) => new(Level.Unknown, Invariant($"{volume.Letter}: the possible growth of {Files(volume.UnknownGrowth.Count)} is not known ({Grouped(Unknowns(volume, facts), d => d.PossibleGrowthBytes.ReasonOrEmpty)}); {free / Gib:0.0} GiB free is above the floor of {floorGib} GiB and the known growth of {known:0.0} GiB")),
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
