using FluentAssertions;

using WslCare.Core.Actions.Engine;
using WslCare.Core.Records;
using WslCare.Core.Status;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Status;

/// <summary>
/// <c>status --json</c>'s <c>lastCleanup</c> (plan §7.2, §15j M7): the NEWEST run in which an action acted and removed or
/// freed something — whatever its trigger, whatever order the lines are in — with what its acting actions removed and freed;
/// <c>available: false</c> with the reason when there is none or the history cannot be read, never a run of 0 bytes.
/// </summary>
public sealed class LastCleanupTests
{
    private static readonly DateTimeOffset Now = FixedTimeProvider.DefaultNow;

    private static RunRecord Run(DateTimeOffset at, RunTrigger trigger, params ActionRecord[] actions) =>
        new(1, RunId.New(at, 7), trigger, at, at.AddMinutes(1), RunOutcome.Completed, actions, null);

    private static ActionRecord Ran(string id, int count, long freed) => new(id, count, freed) { Status = ActionStatus.Ran };

    [Fact]
    public void The_newest_run_with_a_cleanup_is_named_with_its_trigger_and_what_its_acting_actions_removed_and_freed()
    {
        var history = new HistoryRead(
        [
            Run(Now.AddHours(-1), RunTrigger.Manual, Ran("A4", 387, 59_600_000_000), new ActionRecord("A5", 2, 300) { Status = ActionStatus.Failed, Failure = "one refused" }),
            Run(Now.AddHours(-3), RunTrigger.Timer, Ran("A10", 4, 200)),
            Run(Now.AddMinutes(-10), RunTrigger.Cli, Ran("A5", 0, 0)),
            Run(Now.AddMinutes(-5), RunTrigger.Timer, new ActionRecord("A10", 4, 0) { Status = ActionStatus.DryRun, WouldFreeBytes = 900 }),
        ], 0, string.Empty);

        var last = LastCleanups.From(history);

        last.Should().Be(new LastCleanupReport(true, null, RunId.New(Now.AddHours(-1), 7).Text, Now.AddHours(-1), "manual", 59_600_000_300, 389),
            "a failed action's real deletions count (as in logs); a run that removed nothing and a dry run are no cleanup");
    }

    [Fact]
    public void No_cleanup_yet_is_unavailable_with_the_reason_never_a_run_of_zero()
    {
        var last = LastCleanups.From(new HistoryRead([Run(Now, RunTrigger.Cli, Ran("A5", 0, 0))], 0, string.Empty));

        last.Available.Should().BeFalse();
        last.Reason.Should().Be(LastCleanups.NoneYet);
        last.FreedBytes.Should().BeNull();
        last.RunId.Should().BeNull();
    }

    [Fact]
    public void A_history_that_cannot_be_read_is_unavailable_with_its_problem()
    {
        var last = LastCleanups.From(new HistoryRead([], 0, "/var/lib/wsl-care/history.jsonl could not be read: denied"));

        last.Available.Should().BeFalse();
        last.Reason.Should().Contain("could not be read");
    }
}
