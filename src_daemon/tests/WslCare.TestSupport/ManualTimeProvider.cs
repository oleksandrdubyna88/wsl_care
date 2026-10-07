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
            due.Fire();
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

            due.Begin();
            return due;
        }
    }

    /// <summary>A timer of a clock that <see cref="DrivesTimers"/>: its due time is on the clock's monotonic reading.</summary>
    /// <remarks>Held to what the system's timer was SEEN to do (<c>ManualTimeProviderTests</c> runs the contract over both): a
    /// time below -1 ms or above the longest the system takes is refused; <see cref="Change"/> after disposal schedules nothing
    /// and answers false — <see cref="PeriodicTimer"/> reads that false as "disposed"; and <see cref="DisposeAsync"/> completes
    /// only once a callback already picked to fire has returned. A callback counts as picked from the moment an
    /// <see cref="Advance"/> selects it under the clock's lock, so a disposal between the pick and the call still waits for it
    /// (the retro round over PR #20, 2026-10-07).</remarks>
    private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
    {
        /// <summary>The longest due time or period the system's timer takes: 0xfffffffe ms.</summary>
        private static readonly TimeSpan Longest = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

        private bool _disposed;

        /// <summary>Callbacks picked to fire and not yet returned, and what <see cref="DisposeAsync"/> waits on — both under the
        /// clock's lock.</summary>
        private int _running;
        private TaskCompletionSource _idle = Idle();

        public TimeSpan DueAt { get; set; }

        public TimeSpan Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Refuse(dueTime, nameof(dueTime));
            Refuse(period, nameof(period));
            lock (clock._gate)
            {
                return !_disposed && Reschedule(dueTime, period);
            }
        }

        private static void Refuse(TimeSpan time, string name)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(time, Timeout.InfiniteTimeSpan, name);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(time, Longest, name);
        }

        /// <summary>Under the clock's lock: due <paramref name="dueTime"/> from now, or not scheduled when it is infinite.</summary>
        private bool Reschedule(TimeSpan dueTime, TimeSpan period)
        {
            clock._timers.Remove(this);
            Period = period;
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                DueAt = clock._monotonic + dueTime;
                clock._timers.Add(this);
            }

            return true;
        }

        /// <summary>Under the clock's lock, when an advance picks this timer to fire.</summary>
        public void Begin()
        {
            if (_running++ == 0)
            {
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        /// <summary>The callback, on the advancing thread and outside the lock (it may make or change a timer).</summary>
        public void Fire()
        {
            try
            {
                callback(state);
            }
            finally
            {
                End();
            }
        }

        private void End()
        {
            lock (clock._gate)
            {
                if (--_running == 0)
                {
                    _idle.TrySetResult();
                }
            }
        }

        public void Dispose()
        {
            lock (clock._gate)
            {
                Retire();
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (clock._gate)
            {
                Retire();
                return new ValueTask(_idle.Task);
            }
        }

        private void Retire()
        {
            _disposed = true;
            clock._timers.Remove(this);
        }

        private static TaskCompletionSource Idle()
        {
            var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            idle.SetResult();
            return idle;
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
