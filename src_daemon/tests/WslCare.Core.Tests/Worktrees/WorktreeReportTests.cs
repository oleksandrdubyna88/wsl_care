using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Json;
using WslCare.Core.Worktrees;

namespace WslCare.Core.Tests.Worktrees;

/// <summary>
/// E19.S3 (PLAN_disk_hygiene_windows.md §3.3 "The report", E19 stories §3 decision 10): the report a scan makes — counts per kind, idle,
/// build output by where it lies, what could not be established and why, the largest folders BY NAME, never contents — and the one
/// thing it never says in this story: a reclaimable byte figure. Pure over a hand-made scan result.
/// </summary>
public sealed class WorktreeReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static readonly WorktreeSettings Settings = WorktreeWorld.DefaultSettings() with { ReportTop = 2, IdleDays = 3 };

    private static FolderFacts Facts(string name, FolderKind kind, bool locked = false, bool leftover = false) =>
        new(name, $@"D:\w\{name}", kind, locked, locked ? "keep" : string.Empty, leftover, string.Empty, string.Empty);

    private static BuildCandidate Candidate(string name, long bytes, string retained = "", string relative = "bin") =>
        new($@"D:\w\{name}\{relative}", relative, relative.Split('\\')[^1], bytes, 3, retained);

    private static FolderScan Walked(FolderFacts facts, IdleKind idle, long bytes, params BuildCandidate[] candidates) =>
        new(facts, new IdleState(idle, idle == IdleKind.Unknown ? "the walk stopped after 4 entries; the figures are a lower bound" : string.Empty, Now.AddDays(-30)), bytes, candidates);

    private static WorktreeScanResult Result(params RootScan[] roots) => new(Now, TimeSpan.FromSeconds(12.5), true, roots);

    private static WorktreeReport Report(WorktreeScanResult result) => WorktreeReports.From(result, Settings);

    private static RootScan SampleRoot() => new(@"D:\w",
        string.Empty,
        [
            Facts("main", FolderKind.MainClone), Facts("docs", FolderKind.NotGit), Facts("fx", FolderKind.ForeignPointer), Facts("lnk", FolderKind.Link),
            Facts("wt-idle", FolderKind.Worktree), Facts("wt-locked", FolderKind.Worktree, locked: true), Facts("wt-active", FolderKind.Worktree),
            Facts("wt-unknown", FolderKind.Worktree), Facts("gone", FolderKind.Orphan), Facts("testsToDelete-x", FolderKind.Worktree, leftover: true),
        ],
        [
            Walked(Facts("wt-idle", FolderKind.Worktree), IdleKind.Idle, 1000, Candidate("wt-idle", 600), Candidate("wt-idle", 100, "holds a file that is not a build artifact: local.db", @"src\Lib\bin")),
            Walked(Facts("wt-locked", FolderKind.Worktree, locked: true), IdleKind.Idle, 2000, Candidate("wt-locked", 1500)),
            Walked(Facts("wt-active", FolderKind.Worktree), IdleKind.Active, 500, Candidate("wt-active", 300)),
            Walked(Facts("wt-unknown", FolderKind.Worktree), IdleKind.Unknown, 50, Candidate("wt-unknown", 10)),
            Walked(Facts("gone", FolderKind.Orphan), IdleKind.Idle, 4000, Candidate("gone", 2500), Candidate("gone", 200, "holds a nested repository (.git)", "obj")),
            Walked(Facts("testsToDelete-x", FolderKind.Worktree, leftover: true), IdleKind.Idle, 70),
        ]);

    [Fact]
    public void Counts_are_per_kind_with_the_flags_alongside()
    {
        var root = Report(Result(SampleRoot())).Roots.Single();

        (root.Path, root.Folders, root.Worktrees, root.Locked, root.Orphans, root.ForeignPointers, root.MainClones, root.NotGit, root.Links, root.TestLeftovers)
            .Should().Be((@"D:\w", 10, 5, 1, 1, 1, 1, 1, 1, 1));
        root.Refused.Should().BeNull();
    }

    [Fact]
    public void Idle_over_counts_only_folders_established_idle_and_their_size()
    {
        var root = Report(Result(SampleRoot())).Roots.Single();

        root.IdleOver.Should().Be(new IdleOver(3, 4, 1000 + 2000 + 4000 + 70));
        root.SizeBytes.Should().Be(1000 + 2000 + 500 + 50 + 4000 + 70, "every walked folder's logical bytes, whatever its idleness");
    }

    [Fact]
    public void Unknown_is_counted_with_its_reason_and_never_in_idle()
    {
        var root = Report(Result(SampleRoot())).Roots.Single();

        root.Unknown.Count.Should().Be(1);
        root.Unknown.Why.Should().Contain("4 entries");
    }

    [Fact]
    public void Build_output_is_bucketed_by_where_it_lies_and_the_retained_are_apart()
    {
        var build = Report(Result(SampleRoot())).Roots.Single().BuildOutput;

        build.All.Should().Be(new Tally(7, 600 + 100 + 1500 + 300 + 10 + 2500 + 200));
        build.InIdleUnlocked.Should().Be(new Tally(1, 600), "an idle, unlocked worktree's eligible build output");
        build.InIdleLocked.Should().Be(new Tally(1, 1500));
        build.InOrphans.Should().Be(new Tally(1, 2500));
        build.Retained.Should().BeEquivalentTo(
            [new RetainedGroup("holds a file that is not a build artifact", 1, 100), new RetainedGroup("holds a nested repository (.git)", 1, 200)],
            "grouped by the reason's kind: the file's name is not part of the group");
    }

    [Fact]
    public void A_retained_candidate_is_named_with_its_reason_up_to_the_top_setting()
    {
        var named = Report(Result(SampleRoot())).Roots.Single().NamedRetentions;

        named.Should().HaveCount(2);
        named.Should().Contain(n => n.Folder == "wt-idle" && n.Candidate == @"src\Lib\bin" && n.Reason.Contains("local.db"));
        named.Should().Contain(n => n.Folder == "gone" && n.Candidate == "obj" && n.Reason.Contains("nested repository"));
    }

    [Fact]
    public void The_named_retentions_are_bounded_by_the_report_top_setting()
    {
        var many = new RootScan(@"D:\w", string.Empty, [Facts("a", FolderKind.Worktree)],
            [Walked(Facts("a", FolderKind.Worktree), IdleKind.Idle, 10, Candidate("a", 1, "x: one", "bin"), Candidate("a", 1, "x: two", "obj"), Candidate("a", 1, "x: three", "node_modules"))]);

        Report(Result(many)).Roots.Single().NamedRetentions.Should().HaveCount(2);
    }

    [Fact]
    public void The_largest_folders_are_listed_by_name_largest_build_output_first_up_to_the_top_setting()
    {
        var report = Report(Result(SampleRoot()));

        report.Top.Select(t => t.Folder).Should().Equal("gone", "wt-locked");
        report.Top[0].Should().Be(new TopFolder(@"D:\w", "gone", "orphan", false, "idle", 2700, 4000));
        report.Top[1].Locked.Should().BeTrue();
    }

    [Fact]
    public void The_report_never_carries_a_reclaimable_figure_in_this_story()
    {
        var report = Report(Result(SampleRoot()));

        report.Reclaimable.Available.Should().BeFalse();
        report.Reclaimable.Reason.Should().Contain("git has not confirmed the candidates yet (E19.S4)");
        var json = JsonSerializer.Serialize(report, WslCareJsonContext.Default.WorktreeReport);
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("reclaimable").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("available", "reason");
    }

    [Fact]
    public void The_report_names_folders_never_files()
    {
        var json = JsonSerializer.Serialize(Report(Result(SampleRoot())), WslCareJsonContext.Default.WorktreeReport);

        json.Should().Contain("\"gone\"").And.NotContain("Program.cs");
    }

    [Fact]
    public void The_header_carries_the_schema_the_instant_the_elapsed_time_and_whether_it_finished()
    {
        var report = WorktreeReports.From(new WorktreeScanResult(Now, TimeSpan.FromSeconds(12.5), false, [SampleRoot()]), Settings);

        report.SchemaVersion.Should().Be(Core.SchemaVersion.Current);
        report.ScannedAt.Should().Be(Now);
        report.ElapsedSeconds.Should().Be(12.5);
        report.Complete.Should().BeFalse();
    }

    [Fact]
    public void A_refused_root_is_reported_with_its_reason_and_no_figures()
    {
        var refused = new RootScan(@"D:\w", "profile: it holds the profile", [], []);

        var root = Report(Result(refused)).Roots.Single();

        root.Refused.Should().Contain("profile");
        (root.Folders, root.Worktrees, root.SizeBytes).Should().Be((0, 0, 0L));
    }

    [Fact]
    public void A_report_round_trips_through_its_json()
    {
        var report = Report(Result(SampleRoot()));

        var back = JsonSerializer.Deserialize(JsonSerializer.Serialize(report, WslCareJsonContext.Default.WorktreeReport), WslCareJsonContext.Default.WorktreeReport);

        back.Should().BeEquivalentTo(report);
    }
}
