namespace WslCare.TestSupport;

/// <summary>A clock a test moves by hand, UTC: what a wait "took" is what the test advanced — so a backoff of five
/// minutes is asserted without a real five minutes passing.</summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public void Advance(TimeSpan by)
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
