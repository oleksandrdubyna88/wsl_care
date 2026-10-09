using FluentAssertions;

using WslCare.Core.Files;
using WslCare.Core.Records;

namespace WslCare.Cli.Tests;

/// <summary>
/// Plan E14 S2b: the watch holds THE run lock for the seconds of its sample every few minutes, so the 4-hour timer's full run that
/// fires in those seconds waits for it (<c>requests.lockWaitSeconds</c>, the bound an accepted detached run waits) rather than being
/// lost until the next slot. A terminal's <c>collect</c> still refuses at once.
/// </summary>
public sealed class TimerLockWaitTests
{
    [Fact]
    public async Task The_timers_full_run_waits_for_a_lock_the_watch_holds_for_seconds_and_a_terminals_refuses_at_once()
    {
        using var h = new DetachedRunHarness("timer-lock-wait");
        var held = (ExclusiveLock.Held)RunLock.TryTake(h.Sandbox.Paths, h.Sandbox.Files);
        var terminal = CliRun.Guarded(h.Host(), CancellationToken.None, "collect").Exit;
        var release = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            held.Handle.Dispose();
        }, TestContext.Current.CancellationToken);

        var timer = CliRun.Guarded(h.Host(), CancellationToken.None, "collect", "--timer").Exit;
        await release;

        terminal.Should().Be((int)ExitCode.Busy, "a terminal's collect never waits");
        timer.Should().Be((int)ExitCode.Ok, "the timer's run waited out the watch's sample and recorded itself");
        h.History().Should().Contain(r => r.Trigger == RunTrigger.Timer && r.Kind == RunKind.Collect && r.Outcome == RunOutcome.Completed, "the full run is recorded, not lost to the next slot");
    }
}
