using WslCare.Core.Collectors;
using WslCare.Core.Docker;

namespace WslCare.Core.Events;

/// <summary>What a backfill knows about the engine (<see cref="DockerCommands.EngineStart"/>): the instance answering now, and
/// the one the last coverage marker recorded — either may be unavailable, with its reason.</summary>
public sealed record EngineEvidence(Reading<EngineMark> Now, Reading<EngineMark> AtLastCoverage)
{
    /// <summary>Nothing read: the continuity rule falls back to the oldest buffered event alone.</summary>
    public static readonly EngineEvidence Unknown = new(Reading.Missing<EngineMark>("the engine's start was not read"), Reading.Missing<EngineMark>("no engine was recorded"));
}

/// <summary>
/// The continuity rule's evidence for one backfill (<see cref="Coverage.Plan(DateTimeOffset?, IReadOnlyList{DockerEvent}, DateTimeOffset, EngineEvidence)"/>):
/// how far back the buffer is PROVEN complete, and — when that is not far enough — why.
/// </summary>
/// <param name="Oldest">The oldest buffered event; <c>null</c> when the buffer answered nothing.</param>
/// <param name="CompleteFrom">The instant from which every event is in the buffer; <c>null</c> when nothing proves any.</param>
/// <param name="Cause">Why coverage could not reach the anchor, for the gap marker.</param>
internal sealed record Continuity(DateTimeOffset? Oldest, DateTimeOffset? CompleteFrom, string Cause)
{
    public static Continuity Of(DateTimeOffset? anchor, IReadOnlyList<DockerEvent> buffered, EngineEvidence engine, DateTimeOffset windowStart)
    {
        DateTimeOffset? oldest = buffered.Count == 0 ? null : buffered.Min(e => e.At);
        var full = buffered.Count >= Coverage.FullAt;
        var started = Started(engine, anchor);
        DateTimeOffset? sinceStart = !full && started is Reading<DateTimeOffset>.Available { Value: var s } ? (s > windowStart ? s : windowStart) : null;
        return new Continuity(oldest, Earliest(oldest, sinceStart), CauseOf(anchor, buffered.Count, oldest, full, started));
    }

    /// <summary>The engine's start, when it can be trusted as the start of THIS buffer: unavailable when it could not be read,
    /// or when a DIFFERENT engine answers than the last marker recorded while claiming to have started before that marker.</summary>
    private static Reading<DateTimeOffset> Started(EngineEvidence engine, DateTimeOffset? anchor) => (engine.Now, engine.AtLastCoverage) switch
    {
        (Reading<EngineMark>.Unavailable now, _) => Reading.Missing<DateTimeOffset>(now.Reason),
        (Reading<EngineMark>.Available { Value: var now }, Reading<EngineMark>.Available { Value: var then })
            when now.Id != then.Id && now.StartedAt <= (anchor ?? DateTimeOffset.MaxValue) =>
            Reading.Missing<DateTimeOffset>($"a different Docker engine answers than at the last coverage (bridge {Short(then.Id)} then, {Short(now.Id)} now)"),
        (Reading<EngineMark>.Available { Value: var now }, _) => Reading.Of(now.StartedAt),
        _ => throw new System.Diagnostics.UnreachableException("Reading is a closed set"),
    };

    private static DateTimeOffset? Earliest(DateTimeOffset? a, DateTimeOffset? b) => (a, b) switch
    {
        ({ } x, { } y) => x < y ? x : y,
        _ => a ?? b,
    };

    private static string CauseOf(DateTimeOffset? anchor, int count, DateTimeOffset? oldest, bool full, Reading<DateTimeOffset> started) => started switch
    {
        Reading<DateTimeOffset>.Available { Value: var s } when anchor is { } a && s > a => $"the Docker engine restarted at {s:u}: its in-memory event buffer began empty then",
        Reading<DateTimeOffset>.Available when full => $"Docker's event buffer is full ({count} events, it keeps {Coverage.BufferCapacity}) and reaches back only to {oldest:u}: more events happened than it keeps",
        Reading<DateTimeOffset>.Unavailable u => Unproven(oldest, u.Reason),
        _ => "Docker's event buffer does not reach back to the last coverage",
    };

    /// <summary>E2.S3's conservative words, for an engine whose start cannot be used.</summary>
    private static string Unproven(DateTimeOffset? oldest, string why) => oldest is { } o
        ? $"Docker's event buffer reaches back only to {o:u}: the daemon restarted, or more events happened than it keeps ({why})"
        : $"Docker's event buffer is empty: the daemon restarted, or it kept nothing of the gap ({why})";

    private static string Short(string id) => id.Length > 12 ? id[..12] : id;
}
