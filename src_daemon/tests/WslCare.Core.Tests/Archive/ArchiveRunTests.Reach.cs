using FluentAssertions;

using WslCare.Core.Archive;
using WslCare.Core.Files;

namespace WslCare.Core.Tests.Archive;

/// <summary>Plan §15r D1, E9.S4 — <c>archive reach</c>, the short child A13 starts first: the side's lock taken, the base reached,
/// nothing else (no key made, no lease on the base, nothing copied); and <c>archive preview</c> now counts the removals due.</summary>
public sealed partial class ArchiveRunTests
{
    [Fact]
    public void A_reach_takes_the_sides_lock_and_answers_done_without_making_a_key_or_a_lease()
    {
        Session("s1");

        var report = ArchiveRun.Run(Input(Config()) with { ReachOnly = true });

        report.Outcome.Should().Be(RunOutcomes.Done);
        report.Agents.Should().BeEmpty();
        BaseFiles.Should().BeEmpty("a reach writes nothing on the base, not even its lease");
        File.Exists(Path.Combine(new ArchiveState(_sandbox.Paths, _sandbox.Files).Folder, "index.key")).Should().BeFalse("no key is made");
    }

    [Fact]
    public void A_reach_of_a_base_that_does_not_answer_says_unreachable()
    {
        var report = ArchiveRun.Run(Input(Config()) with { ReachOnly = true, Reachable = static (_, _) => false });

        report.Outcome.Should().Be(RunOutcomes.Unreachable);
    }

    /// <summary>9/9.4 #1: the reach takes the side's lock BEFORE it touches the base — so a reach stuck in the kernel holds the lock,
    /// and a run started beside it answers busy instead of sticking too.</summary>
    [Fact]
    public void A_reach_while_the_sides_lock_is_held_answers_busy_and_never_asks_the_base()
    {
        var asked = false;
        var state = new ArchiveState(_sandbox.Paths, _sandbox.Files);
        state.EnsureFolder();
        var held = _sandbox.Files.TryLockExclusive(state.LockFile).Should().BeOfType<ExclusiveLock.Held>().Subject;
        using var handle = held.Handle;

        var report = ArchiveRun.Run(Input(Config()) with { ReachOnly = true, Reachable = (_, _) => asked = true });

        report.Outcome.Should().Be(RunOutcomes.Busy);
        asked.Should().BeFalse();
    }

    [Fact]
    public void The_preview_counts_the_archived_sessions_due_to_be_removed()
    {
        Session("s1");
        ArchiveRun.Run(Input(Config(), runId: "r1")).Agents.Single().Copied.Should().Be(1);
        var input = new SelectionInput(_sandbox.Paths, _sandbox.Files, Config(), Now, TimeZoneInfo.Utc, InUseView.NotChecked("a test"), static _ => null);

        var young = ArchivePreview.From(input, Selection.Select(input), "wsl-host-distro");
        var due = ArchivePreview.From(input with { Now = Now.AddHours(25) }, Selection.Select(input), "wsl-host-distro");

        young.RemovalsDue.Should().Be(0, "archived just now: not due before archive.removeAfterHours");
        due.RemovalsDue.Should().Be(1);
    }
}
