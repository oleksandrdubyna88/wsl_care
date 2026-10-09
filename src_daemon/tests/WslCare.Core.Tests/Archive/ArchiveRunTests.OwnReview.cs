using FluentAssertions;

using WslCare.Core.Archive;

namespace WslCare.Core.Tests.Archive;

/// <summary>Plan §15r *E9.S2b own review round* — the run: phase 2 inside the budget, the entries of an agent no longer archived, the
/// stop told by its kind.</summary>
public sealed partial class ArchiveRunTests
{
    /// <summary>Correctness review M4: phase 2 shares the run's budget — a run with no time left removes nothing and says why.</summary>
    [Fact]
    public void Phase_2_stops_at_the_budget_and_removes_nothing_past_it()
    {
        var source = Session("s1");
        ArchiveRun.Run(Input(Config())).Agents.Single().Copied.Should().Be(1);
        _clock.Advance(TimeSpan.FromHours(25));

        var report = ArchiveRun.Run(Input(Config(), runId: "r2") with { Budget = TimeSpan.Zero });

        report.Outcome.Should().Be(RunOutcomes.Stopped);
        report.StopKind.Should().Be(StopKinds.Budget);
        report.Agents.Single().Removed.Should().Be(0);
        File.Exists(source).Should().BeTrue("the removal waits for a run with time");
    }

    /// <summary>Review m2: an archived session of an agent <c>archive.agents</c> no longer names is let go — dropped from the in-flight
    /// file with a note, its source untouched — never stranded there for ever.</summary>
    [Fact]
    public void An_archived_session_of_an_agent_no_longer_archived_is_let_go_and_its_source_stays()
    {
        var source = Session("s1");
        ArchiveRun.Run(Input(Config())).Agents.Single().Copied.Should().Be(1);
        _clock.Advance(TimeSpan.FromHours(25));

        var report = ArchiveRun.Run(Input(Config(", \"agents\": [\"codex\"]"), runId: "r2") with { OnlyAgent = string.Empty });

        report.Reconcile.Notes.Should().Contain(n => n.Contains("no longer in archive.agents", StringComparison.Ordinal));
        File.Exists(source).Should().BeTrue();
        new ArchiveState(_sandbox.Paths, _sandbox.Files).Inflight().Entries.Should().BeEmpty();
    }

    /// <summary>Review m4: a recorded mount that no longer reads is a refusal — never silently recorded again — and the refusal names
    /// the file and the way out.</summary>
    [Fact]
    public void A_recorded_mount_that_does_not_read_refuses_the_run_and_names_the_way_out()
    {
        Session("s1");
        var state = new ArchiveState(_sandbox.Paths, _sandbox.Files);
        state.EnsureFolder();
        File.WriteAllText(state.BaseFile, "{ torn");

        var report = ArchiveRun.Run(Input(Config()));

        report.Outcome.Should().Be(RunOutcomes.Refused);
        report.Stop.Should().Contain("could not be read").And.Contain("base.json");
        File.ReadAllText(state.BaseFile).Should().Be("{ torn", "it is never recorded over");
    }

    /// <summary>Review m6: a base this account may not write stops the run before any copy (the lease cannot be made).</summary>
    [Fact]
    public void A_read_only_base_stops_the_run_before_any_copy()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && Environment.UserName != "root", "a mode bit stops a normal user on Linux; root writes anyway");
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Session("s1");
        File.SetUnixFileMode(On(Base), UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var report = ArchiveRun.Run(Input(Config()));

            report.Outcome.Should().Be(RunOutcomes.Refused);
            BaseFiles.Should().BeEmpty();
        }
        finally
        {
            File.SetUnixFileMode(On(Base), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Correctness review M9: a stop is told by its KIND — a month index that cannot be written is a fault (the exit is
    /// non-zero), never mistaken for a limit by its sentence.</summary>
    [Fact]
    public void A_run_stopped_by_an_index_it_cannot_write_says_it_was_a_fault()
    {
        Session("s1");
        var index = On($"{Base}/claude-code/2026/08/{SideName.OfThisProcess(Core.Hosting.HostSide.Wsl)}/{ArchiveIndex.FileName}");
        Directory.CreateDirectory(index);

        var report = ArchiveRun.Run(Input(Config()));

        report.Outcome.Should().Be(RunOutcomes.Stopped);
        report.StopKind.Should().Be(StopKinds.IndexWrite);
        StopKinds.IsFault(report.StopKind).Should().BeTrue();
    }

    /// <summary>The S4 gate round, findings 1 and 3: an input whose base is judged late reads, before its bounded window, as NOT JUDGED —
    /// refused under its own rule — never as "no base" nor as a base to use.</summary>
    [Fact]
    public void A_base_read_before_its_window_is_not_judged_never_no_base()
    {
        var early = ArchiveRun.BaseProblem(Input(Config()) with { JudgedBase = BaseFolderRules.NotYetJudged });

        early.Outcome.Should().Be(RunOutcomes.Refused);
        early.Why.Should().Contain(BaseFolderRule.NotJudged).And.NotContain("no archive.baseFolder");
    }
}
