using FluentAssertions;

using WslCare.Core.Archive;
using WslCare.Core.Mcp;

namespace WslCare.Core.Tests.Archive;

/// <summary>Plan §15r, the E9.S5 amendment's plan round, finding 0: the Windows idle rule through a whole run — the selection, phase 1
/// and phase 2 — with the view the Windows side builds over a process table showing a live Claude Code and a Restart Manager that
/// answers free (the CLI flow uses the real process table on purpose, which cannot be made to show a Claude Code).</summary>
public sealed partial class ArchiveRunTests
{
    private static InUseView ClaudeRunsOnWindows() =>
        InUseWindows.View(new GivenAnswers(new RmAnswer.Free()), new GivenTable([new WindowsProcessEntry(40, 4, "claude.exe")], string.Empty), TimeSpan.FromSeconds(5));

    private ArchiveRunInput WhileClaudeRuns(string extra = "", string runId = "r1") =>
        Input(Config(extra), runId: runId) with { InUseScan = static _ => ClaudeRunsOnWindows() };

    [Fact]
    public void While_claude_runs_an_idle_session_is_copied_and_removed_and_a_recent_due_one_stays()
    {
        var idle = Session("idle");
        var recent = Session("recent");
        File.SetLastWriteTimeUtc(recent, Now.AddDays(-20).UtcDateTime);

        var first = ArchiveRun.Run(WhileClaudeRuns(""", "windowsIdleDays": 30""")).Agents.Single();
        _clock.Advance(TimeSpan.FromHours(25));
        var second = ArchiveRun.Run(WhileClaudeRuns(""", "windowsIdleDays": 30""", runId: "r2")).Agents.Single();

        first.Copied.Should().Be(1, "the idle session moves; the one written 20 days ago is due (14) but inside the 30-day window");
        first.Skipped.Should().Contain(s => s.Rule == SkipRule.AgentWorkingHere);
        second.Removed.Should().Be(1);
        File.Exists(idle).Should().BeFalse("phase 2 removed the idle session's source");
        File.Exists(recent).Should().BeTrue();
    }

    [Fact]
    public void While_claude_runs_a_session_touched_after_the_selection_is_kept_by_phase_2()
    {
        var idle = Session("idle");
        ArchiveRun.Run(WhileClaudeRuns()).Agents.Single().Copied.Should().Be(1);

        _clock.Advance(TimeSpan.FromHours(25));
        File.SetLastWriteTimeUtc(idle, Now.AddHours(24).UtcDateTime);
        var second = ArchiveRun.Run(WhileClaudeRuns(runId: "r2")).Agents.Single();

        second.Removed.Should().Be(0, "it was resumed between the copy and the removal");
        File.Exists(idle).Should().BeTrue();
    }
}
