using FluentAssertions;

using WslCare.Core.Processes;

namespace WslCare.Core.Tests.Processes;

/// <summary>
/// <see cref="Bounded.Run{T}"/>: a lookup's answer within its ceiling, the timed-out answer past it — and, PR #10 retro round
/// (own O4), a lookup that THROWS surfaces its own exception, never an <see cref="AggregateException"/> wrapping it, so a
/// caller that handles the lookup's failure meets the type it handles.
/// </summary>
public sealed class BoundedTests
{
    [Fact]
    public void A_lookup_that_throws_surfaces_its_own_exception_unwrapped()
    {
        var act = () => Bounded.Run<string>(() => throw new IOException("Input/output error"), TimeSpan.FromSeconds(30), "timed out", TestContext.Current.CancellationToken);

        act.Should().ThrowExactly<IOException>().WithMessage("Input/output error");
    }

    [Fact]
    public void A_lookup_that_answers_in_time_is_its_answer_and_one_that_does_not_is_the_timed_out_answer()
    {
        using var never = new ManualResetEventSlim();

        Bounded.Run(() => "found", TimeSpan.FromSeconds(30), "timed out", TestContext.Current.CancellationToken).Should().Be("found");
        Bounded.Run(() => Late(never), TimeSpan.FromMilliseconds(100), "timed out", TestContext.Current.CancellationToken).Should().Be("timed out");
        never.Set();
    }

    [Fact]
    public void The_callers_cancellation_ends_the_wait_as_a_cancellation()
    {
        using var never = new ManualResetEventSlim();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var act = () => Bounded.Run(() => Late(never), TimeSpan.FromSeconds(30), "timed out", cancelled.Token);

        act.Should().Throw<OperationCanceledException>();
        never.Set();
    }

    /// <summary>A lookup that answers only once <paramref name="released"/> is set (or after 30 s) — a share that stopped answering.</summary>
    private static string Late(ManualResetEventSlim released)
    {
        released.Wait(TimeSpan.FromSeconds(30));
        return "late";
    }
}
