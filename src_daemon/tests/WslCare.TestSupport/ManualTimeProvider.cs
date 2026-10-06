namespace WslCare.TestSupport;

/// <summary>A clock a test moves by hand, UTC: what a wait "took" is what the test advanced — so a backoff of five
/// minutes is asserted without a real five minutes passing.</summary>
/// <remarks>With <see cref="SteppedTimestamps"/> its TIMESTAMP — the monotonic clock — moves with <see cref="Advance"/> too, and
/// <see cref="JumpWallClock"/> moves the wall clock alone (a host that slept, a clock step): what a two-clock rule is tested
/// with (E7.S2b review A-M4). Without it the timestamp is the real one, as before.
/// With <see cref="DrivesTimers"/> a timer made on this clock — a <see cref="PeriodicTimer"/> given it — fires only when
/// <see cref="Advance"/> moves the clock past its due time, on the advancing thread: a loop's tick is a step the test takes,
/// never a race with the real clock and the thread pool. Without it timers are the real ones, as before.</remarks>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start;
    private TimeSpan _monotonic = TimeSpan.FromDays(1);

    /// <summary>Whether <see cref="GetTimestamp"/> is this clock's own monotonic reading (moved by <see cref="Advance"/>).</summary>
    public bool SteppedTimestamps { get; init; }

    /// <summary>Whether timers made on this clock fire on <see cref="Advance"/> alone (never on the real clock).</summary>
    public bool DrivesTimers { get; init; }

    /// <summary>How many timers made on this clock are scheduled — so a test advances only once the loop it drives is waiting.</summary>
    public int ActiveTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count;
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (!DrivesTimers)
        {
            return base.CreateTimer(callback, state, dueTime, period);
        }

        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

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

        FireDueTimers();
    }

    /// <summary>Every timer whose due time the clock has reached fires, earliest first, outside the lock (a callback may make
    /// or change a timer); a periodic one is due again a period later and fires again while that is still reached.</summary>
    private void FireDueTimers()
    {
        while (NextDue() is { } due)
        {
            due.Callback(due.State);
        }
    }

    private ManualTimer? NextDue()
    {
        lock (_gate)
        {
            var due = _timers.Where(t => t.DueAt <= _monotonic).OrderBy(t => t.DueAt).FirstOrDefault();
            if (due is null)
            {
                return null;
            }

            if (due.Period <= TimeSpan.Zero || due.Period == Timeout.InfiniteTimeSpan)
            {
                _timers.Remove(due);
            }
            else
            {
                due.DueAt += due.Period;
            }

            return due;
        }
    }

    /// <summary>A timer of a clock that <see cref="DrivesTimers"/>: its due time is on the clock's monotonic reading.</summary>
    private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;

        public object? State { get; } = state;

        public TimeSpan DueAt { get; set; }

        public TimeSpan Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                clock._timers.Remove(this);
                Period = period;
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    return true;
                }

                DueAt = clock._monotonic + dueTime;
                clock._timers.Add(this);
                return true;
            }
        }

        public void Dispose()
        {
            lock (clock._gate)
            {
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
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
