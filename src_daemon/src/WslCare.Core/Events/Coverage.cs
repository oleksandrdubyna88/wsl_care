using WslCare.Core.Docker;

namespace WslCare.Core.Events;

/// <summary>What one backfill decides (plan §15b #0): the starts to record, the ONE unrecoverable gap (or none), and the
/// instant coverage reaches afterwards.</summary>
public sealed record BackfillPlan(IReadOnlyList<DockerEvent> Starts, CoverageLine.Gap? Gap, DateTimeOffset CoveredUntil);

/// <summary>A gap a 24-hour count overlaps, named for the reader.</summary>
public sealed record WindowGap(DateTimeOffset From, DateTimeOffset To, string Reason);

/// <summary>An image and how many of the window's starts used it.</summary>
public sealed record ImageCount(string Image, int Starts);

/// <summary>Container starts in the 24 hours before <see cref="To"/> (plan §4.3): <see cref="Complete"/> only when no
/// gap overlaps the window — otherwise the count is <i>partial</i> and <see cref="Gaps"/> names each gap.</summary>
public sealed record StartsWindow(DateTimeOffset From, DateTimeOffset To, int Starts, int Testcontainers, IReadOnlyList<ImageCount> TopImages, bool Complete, IReadOnlyList<WindowGap> Gaps);

/// <summary>
/// The follower's decisions, PURE so every rule of plan §15b #0 and #8 is a unit test: what a backfill can prove,
/// what a 24-hour count may claim, how long the next wait for Docker is.
/// </summary>
public static class Coverage
{
    /// <summary>Plan §15b #0: the backfill never reaches further back than 24 hours.</summary>
    public static readonly TimeSpan BackfillWindow = TimeSpan.FromHours(24);

    /// <summary>The window a count covers (plan §4.3: "started in the last 24 h").</summary>
    public static readonly TimeSpan CountWindow = TimeSpan.FromHours(24);

    /// <summary>Plan §15b #8: the wait for the Docker socket starts at 5 s …</summary>
    public static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(5);

    /// <summary>… and doubles up to 5 minutes.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    /// <summary>How old the newest coverage may be before a count treats the follower as not running: one stream
    /// segment and five minutes.</summary>
    public static readonly TimeSpan Staleness = EventsFollower.SegmentLength + TimeSpan.FromMinutes(5);

    public const int TopImageCount = 5;

    /// <summary>The instant up to which every start is recorded, read from the lines; <c>null</c> when nothing ever was.</summary>
    public static DateTimeOffset? LastCovered(IEnumerable<CoverageLine> lines) =>
        lines.Select(l => l.CoveredUntil).Where(c => c is not null).DefaultIfEmpty(null).Max();

    /// <summary>The next wait for the Docker socket: 5 s first, then doubled, never above 5 minutes (plan §15b #8).</summary>
    public static TimeSpan NextBackoff(TimeSpan? previous) =>
        previous is not { } last ? FirstBackoff : last * 2 > MaxBackoff ? MaxBackoff : last * 2;

    /// <summary>
    /// What a backfill at <paramref name="now"/> can prove, given the last coverage <paramref name="anchor"/> and every
    /// event Docker still buffered since <c>now − 24 h</c> (all kinds — <see cref="DockerCommands.Backfill"/>).
    /// </summary>
    /// <remarks>
    /// Docker keeps its events in memory only (a few hundred), so "nothing came back" is not "nothing happened". A gap
    /// is filled ONLY when the buffer demonstrably reaches back past the anchor: its oldest event is at or before it.
    /// Otherwise the stretch from the anchor to the oldest buffered event is ONE unrecoverable gap — the daemon
    /// restarted, the buffer overflowed, or the anchor is older than the 24-hour window — and the starts after it are
    /// recorded. No anchor at all (the follower's first start) is a gap from the window's start.
    /// </remarks>
    public static BackfillPlan Plan(DateTimeOffset? anchor, IReadOnlyList<DockerEvent> buffered, DateTimeOffset now)
    {
        var windowStart = now - BackfillWindow;
        DateTimeOffset? oldest = buffered.Count == 0 ? null : buffered.Min(e => e.At);
        if (anchor is { } a && a >= windowStart && oldest is { } o && o <= a)
        {
            return new BackfillPlan([.. buffered.Where(e => IsStart(e) && e.At > a).OrderBy(e => e.At)], null, now);
        }

        var from = anchor ?? windowStart;
        var to = oldest is { } first && first > from ? first : oldest is null ? now : from;
        var gap = to > from ? new CoverageLine.Gap(from, to, GapReason(anchor, oldest, windowStart)) : null;
        return new BackfillPlan([.. buffered.Where(e => IsStart(e) && e.At >= to && (anchor is null || e.At > anchor)).OrderBy(e => e.At)], gap, now);
    }

    /// <summary>Container starts in the 24 hours before <paramref name="now"/>, complete or partial with the gaps named
    /// (plan §15b #0: complete again only once a whole 24 h lies after a gap's end).</summary>
    public static StartsWindow Last24h(IReadOnlyList<CoverageLine> lines, DateTimeOffset now)
    {
        var from = now - CountWindow;
        var starts = lines.OfType<CoverageLine.Start>().Where(s => s.At > from && s.At <= now).DistinctBy(s => (s.Id, s.At)).ToList();
        var gaps = Gaps(lines, from, now);
        return new StartsWindow(
            from,
            now,
            starts.Count,
            starts.Count(s => s.Testcontainers),
            [.. starts.GroupBy(s => s.Image, StringComparer.Ordinal).Select(g => new ImageCount(g.Key, g.Count())).OrderByDescending(i => i.Starts).ThenBy(i => i.Image, StringComparer.Ordinal).Take(TopImageCount)],
            gaps.Count == 0,
            gaps);
    }

    /// <summary>Every gap the window <paramref name="from"/> .. <paramref name="now"/> overlaps: the recorded ones, the
    /// stretch before the first record, and the open one after the last coverage when it is stale.</summary>
    private static IReadOnlyList<WindowGap> Gaps(IReadOnlyList<CoverageLine> lines, DateTimeOffset from, DateTimeOffset now)
    {
        var recorded = lines.OfType<CoverageLine.Gap>().Where(g => g.To > from && g.From < now).Select(g => new WindowGap(g.From, g.To, g.Reason));
        var known = lines.SelectMany(l => l is CoverageLine.Gap g ? [g.From] : l.CoveredUntil is { } c ? [c] : Array.Empty<DateTimeOffset>()).ToList();
        IEnumerable<WindowGap> before = known.Count == 0
            ? [new WindowGap(from, now, "nothing has been recorded: the events follower has never run")]
            : known.Min() > from ? [new WindowGap(from, known.Min(), "nothing was recorded before this: the follower has not run for 24 h yet")] : [];
        IEnumerable<WindowGap> open = LastCovered(lines) is { } last && now - last > Staleness
            ? [new WindowGap(last, now, "nothing recorded since: the follower is not running, or Docker is unreachable")]
            : [];
        return [.. before.Concat(recorded).Concat(open).OrderBy(g => g.From)];
    }

    private static string GapReason(DateTimeOffset? anchor, DateTimeOffset? oldest, DateTimeOffset windowStart) => (anchor, oldest) switch
    {
        (null, null) => "no record before the follower's first start, and Docker's event buffer is empty",
        (null, { } o) => $"no record before the follower's first start; Docker's event buffer reaches back to {o:u}",
        ({ } a, _) when a < windowStart => "the gap is older than the 24 h backfill window",
        (_, null) => "Docker's event buffer is empty: the daemon restarted, or it kept nothing of the gap",
        (_, { } o) => $"Docker's event buffer reaches back only to {o:u}: the daemon restarted, or more events happened than it keeps",
    };

    internal static bool IsStart(DockerEvent e) =>
        string.Equals(e.Type, "container", StringComparison.Ordinal) && string.Equals(e.Action, "start", StringComparison.Ordinal);
}
