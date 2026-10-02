namespace WslCare.Core.Events;

/// <summary>
/// One line of <c>{state}/container-starts/{yyyy-MM-dd}.jsonl</c> (plan §4.3, §15 #9), as the domain reads it — a
/// closed set. Each kind says something about COVERAGE: up to which instant every container start is known.
/// </summary>
public abstract record CoverageLine
{
    private CoverageLine()
    {
    }

    /// <summary>The instant the line is filed under (its day file) and ordered by.</summary>
    public abstract DateTimeOffset Instant { get; }

    /// <summary>The instant up to which this line proves coverage; <c>null</c> for a line that proves none (a start marker).</summary>
    public abstract DateTimeOffset? CoveredUntil { get; }

    /// <summary>A container started (plan §4.3: image, name, the Testcontainers label).</summary>
    /// <param name="Backfilled">Recovered from Docker's buffer at a start or after an outage, not seen live.</param>
    public sealed record Start(DateTimeOffset At, string Id, string Name, string Image, bool Testcontainers, bool Backfilled) : CoverageLine
    {
        public override DateTimeOffset Instant => At;

        public override DateTimeOffset? CoveredUntil => At;
    }

    /// <summary>Every start up to <paramref name="At"/> is recorded: a stream segment ended normally, or a backfill completed.</summary>
    public sealed record Covered(DateTimeOffset At) : CoverageLine
    {
        public override DateTimeOffset Instant => At;

        public override DateTimeOffset? CoveredUntil => At;
    }

    /// <summary>The follower started (no coverage of its own).</summary>
    public sealed record FollowerStarted(DateTimeOffset At, int Pid) : CoverageLine
    {
        public override DateTimeOffset Instant => At;

        public override DateTimeOffset? CoveredUntil => null;
    }

    /// <summary>The follower stopped; <paramref name="Reached"/> is how far its coverage reached — not the stop
    /// instant, which during an outage is later.</summary>
    public sealed record FollowerStopped(DateTimeOffset At, int Pid, DateTimeOffset? Reached) : CoverageLine
    {
        public override DateTimeOffset Instant => At;

        public override DateTimeOffset? CoveredUntil => Reached;
    }

    /// <summary>ONE unrecoverable gap (plan §15b #0): the starts between <paramref name="From"/> and <paramref name="To"/>
    /// are unknown, and why. Coverage resumes at <paramref name="To"/>.</summary>
    public sealed record Gap(DateTimeOffset From, DateTimeOffset To, string Reason) : CoverageLine
    {
        public override DateTimeOffset Instant => To;

        public override DateTimeOffset? CoveredUntil => To;
    }
}

/// <summary>The JSON shape of a <see cref="CoverageLine"/> — flat, with a <c>kind</c>; the members a kind does not use
/// are absent. Nullable only here, at the file's edge.</summary>
public sealed record CoverageLineJson(string Kind)
{
    public DateTimeOffset? At { get; init; }

    public string? Id { get; init; }

    public string? Name { get; init; }

    public string? Image { get; init; }

    public bool? Testcontainers { get; init; }

    public bool? Backfilled { get; init; }

    public int? Pid { get; init; }

    public DateTimeOffset? Covered { get; init; }

    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    public string? Reason { get; init; }

    public const string StartKind = "start";
    public const string CoveredKind = "covered";
    public const string StartedKind = "followerStarted";
    public const string StoppedKind = "followerStopped";
    public const string GapKind = "gap";

    public static CoverageLineJson Of(CoverageLine line) => line switch
    {
        CoverageLine.Start s => new(StartKind) { At = s.At, Id = s.Id, Name = s.Name, Image = s.Image, Testcontainers = s.Testcontainers, Backfilled = s.Backfilled ? true : null },
        CoverageLine.Covered c => new(CoveredKind) { At = c.At },
        CoverageLine.FollowerStarted f => new(StartedKind) { At = f.At, Pid = f.Pid },
        CoverageLine.FollowerStopped f => new(StoppedKind) { At = f.At, Pid = f.Pid, Covered = f.Reached },
        CoverageLine.Gap g => new(GapKind) { From = g.From, To = g.To, Reason = g.Reason },
        _ => throw new System.Diagnostics.UnreachableException("CoverageLine is a closed set"),
    };

    /// <summary>The domain line, or nothing for a line that is not one (a torn or unknown line is skipped, never guessed).</summary>
    public IEnumerable<CoverageLine> ToLine() => (Kind, At, From, To) switch
    {
        (StartKind, { } at, _, _) => [new CoverageLine.Start(at, Id ?? string.Empty, Name ?? string.Empty, Image ?? string.Empty, Testcontainers ?? false, Backfilled ?? false)],
        (CoveredKind, { } at, _, _) => [new CoverageLine.Covered(at)],
        (StartedKind, { } at, _, _) => [new CoverageLine.FollowerStarted(at, Pid ?? 0)],
        (StoppedKind, { } at, _, _) => [new CoverageLine.FollowerStopped(at, Pid ?? 0, Covered)],
        (GapKind, _, { } from, { } to) => [new CoverageLine.Gap(from, to, Reason ?? string.Empty)],
        _ => [],
    };
}
