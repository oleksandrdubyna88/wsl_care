using WslCare.Core.Config;
using WslCare.Core.Collectors;
using WslCare.Core.Docker;

namespace WslCare.Core.Events;

/// <summary>What one backfill decides (plan §15b #0): the starts to record, the ONE unrecoverable gap (or none), and the
/// instant coverage reaches afterwards.</summary>
public sealed record BackfillPlan(IReadOnlyList<DockerEvent> Starts, CoverageLine.Gap? Gap, DateTimeOffset CoveredUntil);

/// <summary>A gap a 24-hour count overlaps, named for the reader.</summary>
public sealed record WindowGap(DateTimeOffset From, DateTimeOffset To, string Reason);

/// <summary>
/// <c>{state}/starts-summary.json</c> (gate finding #8): the follower's trailing-24-hour count, written atomically after
/// every marker it appends, so <c>status</c> answers from one small file instead of re-parsing 14 days of JSONL.
/// </summary>
/// <param name="WrittenAt">When the follower computed <paramref name="Window"/>.</param>
/// <param name="LastCovered">How far coverage reached then; <c>null</c> when nothing was ever covered.</param>
public sealed record StartsSummary(int SchemaVersion, DateTimeOffset WrittenAt, DateTimeOffset? LastCovered, StartsWindow Window);

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
    public static TimeSpan FirstBackoff => Tuning.Current.Seconds(ConfigKeys.Events.RetryFirstSeconds);

    /// <summary>… and doubles up to 5 minutes.</summary>
    public static TimeSpan MaxBackoff => Tuning.Current.Seconds(ConfigKeys.Events.RetryMaxSeconds);

    /// <summary>How old the newest coverage may be before a count treats the follower as not running: one stream
    /// segment and five minutes.</summary>
    public static TimeSpan Staleness => EventsFollower.SegmentLength + Tuning.Current.Minutes(ConfigKeys.Events.StalenessSlackMinutes);

    public static int TopImageCount => Tuning.Current.Int(ConfigKeys.Events.TopImages);

    /// <summary>The instant up to which every start is recorded, read from the lines; <c>null</c> when nothing ever was.</summary>
    public static DateTimeOffset? LastCovered(IEnumerable<CoverageLine> lines) =>
        lines.Select(l => l.CoveredUntil).Where(c => c is not null).DefaultIfEmpty(null).Max();

    /// <summary>The engine the newest <c>covered</c> marker that recorded one names — what a backfill compares the engine
    /// answering now with; unavailable when no marker recorded an engine.</summary>
    public static Reading<EngineMark> LastEngine(IEnumerable<CoverageLine> lines) =>
        lines.OfType<CoverageLine.Covered>().Where(c => c.Engine.IsAvailable).OrderBy(c => c.At).LastOrDefault()?.Engine
            ?? EngineEvidence.Unknown.AtLastCoverage;

    /// <summary>The next wait for the Docker socket: 5 s first, then doubled, never above 5 minutes (plan §15b #8).</summary>
    public static TimeSpan NextBackoff(TimeSpan? previous) =>
        previous is not { } last ? FirstBackoff : last * Tuning.Current.Int(ConfigKeys.Events.RetryFactor) > MaxBackoff ? MaxBackoff : last * Tuning.Current.Int(ConfigKeys.Events.RetryFactor);

    /// <summary>What a backfill at <paramref name="now"/> can prove when nothing is known about the engine — the conservative
    /// rule: only the oldest buffered event proves how far back the buffer reaches.</summary>
    public static BackfillPlan Plan(DateTimeOffset? anchor, IReadOnlyList<DockerEvent> buffered, DateTimeOffset now) =>
        Plan(anchor, buffered, now, EngineEvidence.Unknown);

    /// <summary>
    /// What a backfill at <paramref name="now"/> can prove, given the last coverage <paramref name="anchor"/>, every event
    /// Docker still buffered since <c>now − 24 h</c> (all kinds — <see cref="DockerCommands.Backfill"/>) and the engine
    /// instance that answered (<paramref name="engine"/>).
    /// </summary>
    /// <remarks>
    /// <para><b>The continuity rule</b> (plan §15b #0 as amended after the E2 code round, gate finding #2/#7/#9). Docker keeps
    /// its events in a RING of <see cref="BufferCapacity"/> in memory, dropping the oldest first. So the buffer is complete
    /// from its oldest event onward, always; and while it is NOT full, it is complete from the moment the engine started —
    /// nothing has been dropped yet. An empty or short buffer is therefore not by itself proof of loss: events could have
    /// been dropped only when the buffer is FULL (it holds at least <see cref="FullAt"/> events) or when the ENGINE
    /// RESTARTED (a new <see cref="EngineMark"/> started after the last coverage, or a different engine answers). The
    /// proven start of completeness is the earlier of the oldest buffered event and — for a buffer that is not full — the
    /// engine's start (never before the 24 h window, which is all the backfill asks for). Coverage since the anchor holds
    /// when that start is at or before the anchor; otherwise the stretch between them is ONE unrecoverable gap.</para>
    /// <para>Consequences: an idle engine with an empty buffer and no restart is covered with zero starts; a host that slept
    /// (the engine suspended, the same instance after) is covered; a full buffer whose oldest event is newer than the anchor
    /// is a gap up to that event; a restart is a gap from the anchor to the engine's start, and the starts after it are
    /// recorded. When the engine cannot be read the rule falls back to the oldest buffered event alone — what E2.S3 shipped.
    /// No anchor at all (the follower's first start) needs completeness back to the window's start.</para>
    /// </remarks>
    public static BackfillPlan Plan(DateTimeOffset? anchor, IReadOnlyList<DockerEvent> buffered, DateTimeOffset now, EngineEvidence engine)
    {
        var windowStart = now - BackfillWindow;
        var evidence = Continuity.Of(anchor, buffered, engine, windowStart);
        var from = anchor ?? windowStart;
        var reach = evidence.CompleteFrom ?? now;
        var to = reach > Floor(from, windowStart) ? reach : Floor(from, windowStart);
        var gap = to > from ? new CoverageLine.Gap(from, to, GapReason(anchor, evidence, windowStart)) : null;
        var after = anchor ?? DateTimeOffset.MinValue;
        return new BackfillPlan([.. buffered.Where(e => IsStart(e) && e.At > after && (gap is null || e.At >= gap.To)).OrderBy(e => e.At)], gap, now);
    }

    /// <summary>Plan §15b #0's buffer, measured: moby keeps the last 256 events (<c>daemon/events</c>, <c>eventsLimit</c>).</summary>
    public const int BufferCapacity = 256;

    /// <summary>
    /// How many buffered events count as a FULL buffer: within 16 of <see cref="BufferCapacity"/>. Measured 2026-10-02 on
    /// a busy engine: the unfiltered window answered 248, 255 and 255 events — never 256 — so equality would never fire.
    /// A false "full" costs only a conservative gap; a false "not full" would claim a completeness nobody proved, so
    /// the margin errs low.
    /// </summary>
    public const int FullAt = BufferCapacity - 16;

    /// <summary>Coverage before the window cannot be proven by a backfill that only asks for the window.</summary>
    private static DateTimeOffset Floor(DateTimeOffset from, DateTimeOffset windowStart) => from < windowStart ? windowStart : from;

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

    /// <summary>What a summary the follower wrote says at <paramref name="now"/>: its window as computed then (its <c>To</c>
    /// is when), made partial with the open gap when the follower has not covered anything for longer than
    /// <see cref="Staleness"/> — the same claim <see cref="Last24h"/> would make over the raw lines.</summary>
    public static StartsWindow AsOf(StartsSummary summary, DateTimeOffset now)
    {
        var last = summary.LastCovered ?? summary.WrittenAt;
        return now - last > Staleness
            ? summary.Window with { Complete = false, Gaps = [.. summary.Window.Gaps, new WindowGap(last, now, StaleReason)] }
            : summary.Window;
    }

    /// <summary>The count <c>status</c> answers when the follower has written no summary yet.</summary>
    public static StartsWindow NoSummary(DateTimeOffset now) =>
        new(now - CountWindow, now, 0, 0, [], false, [new WindowGap(now - CountWindow, now, NoSummaryReason)]);

    public const string NoSummaryReason = "no 24-hour summary has been written: the events follower has not recorded anything yet";

    private const string StaleReason = "nothing recorded since: the follower is not running, or Docker is unreachable";

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
            ? [new WindowGap(last, now, StaleReason)]
            : [];
        return [.. before.Concat(recorded).Concat(open).OrderBy(g => g.From)];
    }

    private static string GapReason(DateTimeOffset? anchor, Continuity evidence, DateTimeOffset windowStart) => (anchor, evidence.Oldest) switch
    {
        (null, null) => $"no record before the follower's first start, and Docker's event buffer is empty",
        (null, { } o) => $"no record before the follower's first start; Docker's event buffer reaches back to {o:u}",
        ({ } a, _) when a < windowStart => "the gap is older than the 24 h backfill window",
        _ => evidence.Cause,
    };

    internal static bool IsStart(DockerEvent e) =>
        string.Equals(e.Type, "container", StringComparison.Ordinal) && string.Equals(e.Action, "start", StringComparison.Ordinal);
}
