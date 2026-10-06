namespace WslCare.TestSupport;

/// <summary>A clock a test moves by hand, UTC: what a wait "took" is what the test advanced — so a backoff of five
/// minutes is asserted without a real five minutes passing.</summary>
/// <remarks>With <see cref="SteppedTimestamps"/> its TIMESTAMP — the monotonic clock — moves with <see cref="Advance"/> too, and
/// <see cref="JumpWallClock"/> moves the wall clock alone (a host that slept, a clock step): what a two-clock rule is tested
/// with (E7.S2b review A-M4). Without it the timestamp is the real one, as before.</remarks>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now = start;
    private TimeSpan _monotonic = TimeSpan.FromDays(1);

    /// <summary>Whether <see cref="GetTimestamp"/> is this clock's own monotonic reading (moved by <see cref="Advance"/>).</summary>
    public bool SteppedTimestamps { get; init; }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => SteppedTimestamps ? TimeSpan.TicksPerSecond : base.TimestampFrequency;

    public override long GetTimestamp()
    {
        if (!SteppedTimestamps)
        {
            return base.GetTimestamp();
        }

        lock (_gate)
        {
            return _monotonic.Ticks;
        }
    }

    public void Advance(TimeSpan by)
    {
        lock (_gate)
        {
            _now += by;
            _monotonic += by;
        }
    }

    /// <summary>The wall clock alone moves (the monotonic clock does not): a host that slept, a clock stepped forward.</summary>
    public void JumpWallClock(TimeSpan by)
    {
        lock (_gate)
        {
            _now += by;
        }
    }

    /// <summary>A wait that advances this clock by its length and records it — the follower's injected wait.</summary>
    public Func<TimeSpan, CancellationToken, Task> RecordingWait(List<TimeSpan> waits) => (delay, token) =>
    {
        token.ThrowIfCancellationRequested();
        lock (waits)
        {
            waits.Add(delay);
        }

        Advance(delay);
        return Task.CompletedTask;
    };
}
