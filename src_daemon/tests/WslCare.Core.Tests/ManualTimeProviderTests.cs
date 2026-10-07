using FluentAssertions;

using WslCare.TestSupport;

namespace WslCare.Core.Tests;

/// <summary>
/// <see cref="ManualTimeProvider"/>'s timers (<see cref="ManualTimeProvider.DrivesTimers"/>) are harness code under test
/// (generated-code-tests rule §3 — never leave a fake untested, never let it be more permissive than the real thing): a one-shot
/// timer fires once and a periodic one every period the clock passes, several due timers fire earliest first, <c>Change</c>
/// reschedules from now and an infinite due time unschedules, and a disposed timer is gone. Where the contract is the real
/// timer's — <c>Change</c> after <c>Dispose</c>, <c>DisposeAsync</c> beside a running callback, a negative time, a
/// <see cref="PeriodicTimer"/> over the clock — the same test runs over <see cref="TimeProvider.System"/> too, so the fake is
/// held to what the real one was SEEN to do, not to a reading of its documentation (the retro round over PR #20, 2026-10-07).
/// </summary>
public sealed class ManualTimeProviderTests
{
    private const string Real = "the system clock";
    private const string Manual = "a manual clock that drives its timers";

    private static readonly DateTimeOffset Start = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    /// <summary>How long a callback the test started may take to be seen running — a bound on a loaded machine, not a timing.</summary>
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private static ManualTimeProvider Driving() => new(Start) { DrivesTimers = true };

    private static TimeProvider Provider(string clock) => clock == Real ? TimeProvider.System : Driving();

    /// <summary>The whole seconds the clock has moved since <see cref="Start"/>.</summary>
    private static int At(ManualTimeProvider clock) => (int)(clock.GetUtcNow() - Start).TotalSeconds;

    /// <summary>Moves the clock one second at a time, so a callback reads the second it was due at.</summary>
    private static void Step(ManualTimeProvider clock, int seconds)
    {
        for (var i = 0; i < seconds; i++)
        {
            clock.Advance(Second);
        }
    }

    /// <summary>A timer on the clock named, and how to make it fire: the real one fires by itself 50 ms on; the manual one when
    /// the test advances it — on another thread here, as a loop under test would be ticked while the test looks on.</summary>
    private sealed record Armed(ITimer Timer, Func<Task> Fire);

    private static Armed Arm(string clock, TimerCallback callback) => clock == Real
        ? new Armed(TimeProvider.System.CreateTimer(callback, null, TimeSpan.FromMilliseconds(50), Timeout.InfiniteTimeSpan), () => Task.CompletedTask)
        : ArmManual(callback);

    private static Armed ArmManual(TimerCallback callback)
    {
        var clock = Driving();
        return new Armed(clock.CreateTimer(callback, null, Second, Timeout.InfiniteTimeSpan), () => Task.Run(() => clock.Advance(Second)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void A_one_shot_timer_fires_once_at_its_due_time_and_a_periodic_one_every_period_after(int oneShotPeriodMilliseconds)
    {
        // A period of zero is one-shot like an infinite one, as on the real timer.
        var clock = Driving();
        List<int> once = [];
        List<int> every = [];
        using var oneShot = clock.CreateTimer(_ => once.Add(At(clock)), null, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(oneShotPeriodMilliseconds));
        using var periodic = clock.CreateTimer(_ => every.Add(At(clock)), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));

        Step(clock, 15);

        once.Should().Equal([5], "a one-shot timer fires at its due time and never again");
        every.Should().Equal([2, 5, 8, 11, 14], "a periodic timer is due again a period after each firing");
        clock.ActiveTimers.Should().Be(1, "the one-shot timer is no longer scheduled; the periodic one still is");
    }

    [Fact]
    public void A_timer_never_fires_before_the_clock_reaches_its_due_time()
    {
        var clock = Driving();
        var fired = 0;
        using var timer = clock.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);

        clock.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));

        fired.Should().Be(0, "one tick short of the due time is not the due time");
        clock.Advance(TimeSpan.FromTicks(1));
        fired.Should().Be(1);
    }

    [Fact]
    public void Several_timers_due_in_one_advance_fire_earliest_first_a_periodic_one_again_in_its_turn()
    {
        var clock = Driving();
        List<string> order = [];
        using var a = clock.CreateTimer(_ => order.Add("a, due at 3"), null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
        using var b = clock.CreateTimer(_ => order.Add("b, due at 1"), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        using var c = clock.CreateTimer(_ => order.Add("c, every 2"), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

        clock.Advance(TimeSpan.FromSeconds(5));

        order.Should().Equal(["b, due at 1", "c, every 2", "a, due at 3", "c, every 2"], "one advance of 5 s passes 1, 2, 3 and 4, in that order");
    }

    [Fact]
    public void Change_reschedules_from_now_an_infinite_due_time_unschedules_and_a_new_period_takes_over()
    {
        var clock = Driving();
        List<int> fired = [];
        using var timer = clock.CreateTimer(_ => fired.Add(At(clock)), null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
        Step(clock, 3);

        timer.Change(TimeSpan.FromSeconds(4), Timeout.InfiniteTimeSpan).Should().BeTrue();
        Step(clock, 5);
        timer.Change(Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(1)).Should().BeTrue();
        var unscheduled = clock.ActiveTimers;
        Step(clock, 3);
        timer.Change(Second, TimeSpan.FromSeconds(2)).Should().BeTrue();
        Step(clock, 4);

        fired.Should().Equal([7, 12, 14], "4 s from second 3 is second 7, not the first due time 5; nothing while unscheduled; then 1 s from second 11 and every 2 s");
        unscheduled.Should().Be(0, "an infinite due time unschedules the timer whatever its period");
    }

    [Fact]
    public async Task A_disposed_timer_is_unscheduled_and_its_DisposeAsync_with_no_callback_running_completes_at_once()
    {
        var clock = Driving();
        var fired = 0;
        var timer = clock.CreateTimer(_ => fired++, null, Second, Second);
        var other = clock.CreateTimer(_ => { }, null, Second, Second);
        Step(clock, 2);

        timer.Dispose();
        var disposal = other.DisposeAsync();
        Step(clock, 3);

        fired.Should().Be(2, "a periodic timer stops at its disposal");
        clock.ActiveTimers.Should().Be(0);
        disposal.IsCompleted.Should().BeTrue("no callback of that timer was running");
        await disposal;
    }

    [Theory]
    [InlineData(Real)]
    [InlineData(Manual)]
    public void Change_after_Dispose_answers_false_and_schedules_nothing(string clock)
    {
        var fired = 0;
        var armed = Arm(clock, _ => Interlocked.Increment(ref fired));

        armed.Timer.Dispose();

        armed.Timer.Change(Second, Timeout.InfiniteTimeSpan).Should().BeFalse($"over {clock} a disposed timer is not rescheduled — PeriodicTimer reads that false as disposed");
    }

    [Fact]
    public void A_manual_timer_changed_after_its_disposal_never_fires()
    {
        var clock = Driving();
        var fired = 0;
        var timer = clock.CreateTimer(_ => fired++, null, Second, Timeout.InfiniteTimeSpan);
        timer.Dispose();

        timer.Change(Second, Second);
        Step(clock, 3);

        clock.ActiveTimers.Should().Be(0, "a change after disposal re-adds nothing");
        fired.Should().Be(0);
    }

    [Theory]
    [InlineData(Real)]
    [InlineData(Manual)]
    public void A_periodic_timer_whose_period_is_set_after_its_disposal_throws_ObjectDisposedException(string clock)
    {
        var periodic = new PeriodicTimer(TimeSpan.FromSeconds(2), Provider(clock));
        periodic.Dispose();

        var setting = () => periodic.Period = TimeSpan.FromSeconds(3);

        setting.Should().Throw<ObjectDisposedException>($"over {clock} the timer under PeriodicTimer answers false to a change after disposal");
    }

    [Theory]
    [InlineData(Real)]
    [InlineData(Manual)]
    public async Task DisposeAsync_completes_only_once_a_callback_already_running_has_returned(string clock)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = Arm(clock, _ =>
        {
            entered.TrySetResult();
            release.Task.Wait(Ceiling);
        });
        var firing = armed.Fire();
        try
        {
            await entered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);

            var disposal = armed.Timer.DisposeAsync().AsTask();

            disposal.IsCompleted.Should().BeFalse($"over {clock} the callback is still running, and DisposeAsync waits for it");
            release.TrySetResult();
            await disposal.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        }
        finally
        {
            release.TrySetResult();
            await firing.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(Real)]
    [InlineData(Manual)]
    public void A_negative_due_time_or_period_other_than_infinite_is_refused(string clock)
    {
        var provider = Provider(clock);
        var minusTwo = TimeSpan.FromSeconds(-2);

        var due = () => provider.CreateTimer(_ => { }, null, minusTwo, Timeout.InfiniteTimeSpan);
        var period = () => provider.CreateTimer(_ => { }, null, Second, minusTwo);

        due.Should().Throw<ArgumentOutOfRangeException>($"over {clock} a due time below -1 ms is refused");
        period.Should().Throw<ArgumentOutOfRangeException>($"over {clock} a period below -1 ms is refused");
    }

    [Fact]
    public async Task A_periodic_timer_over_the_clock_ticks_when_the_clock_passes_its_period_and_ends_at_its_disposal()
    {
        var clock = Driving();
        var periodic = new PeriodicTimer(TimeSpan.FromSeconds(2), clock);

        var tick = periodic.WaitForNextTickAsync(TestContext.Current.CancellationToken).AsTask();
        clock.Advance(Second);
        var early = tick.IsCompleted;
        clock.Advance(Second);

        early.Should().BeFalse("half the period is not the period");
        (await tick.WaitAsync(Ceiling, TestContext.Current.CancellationToken)).Should().BeTrue("the clock passed the period");
        var next = periodic.WaitForNextTickAsync(TestContext.Current.CancellationToken).AsTask();
        periodic.Dispose();
        (await next.WaitAsync(Ceiling, TestContext.Current.CancellationToken)).Should().BeFalse("a disposed periodic timer ends the wait");
        clock.ActiveTimers.Should().Be(0);
    }

    [Fact]
    public void Without_DrivesTimers_a_timer_is_the_real_one_and_the_clock_schedules_nothing()
    {
        var clock = new ManualTimeProvider(Start);

        using var timer = clock.CreateTimer(_ => { }, null, TimeSpan.FromMinutes(5), Timeout.InfiniteTimeSpan);

        clock.ActiveTimers.Should().Be(0, "the system's timer queue holds it, not this clock");
    }
}
