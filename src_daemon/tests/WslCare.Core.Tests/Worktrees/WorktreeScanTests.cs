using FluentAssertions;

using WslCare.Core.Files;
using WslCare.Core.Worktrees;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Worktrees;

/// <summary>
/// E19.S3 (PLAN_disk_hygiene_windows.md §3.3 "Idle", "Build output", "The walk"; E19 stories §3 decisions 3, 11): the scan over real trees
/// under a <c>testsToDelete-</c> folder — what kind each child is, when a worktree is idle (outside its build output, plus each build
/// folder's own write, plus the admin files), which build-output folders are candidates and which are kept whole, and that a budget, a
/// denied entry or a link never makes a worktree look idle. Windows legs; read-only (nothing is deleted).
/// </summary>
public sealed class WorktreeScanTests
{
    private static WorktreeScanResult Scan(WorktreeWorld world, WorktreeSettings? settings = null, params string[] roots) =>
        world.Scanner().Run(roots.Length == 0 ? [world.Work] : roots, settings ?? WorktreeWorld.DefaultSettings(), CancellationToken.None);

    private static FolderScan Walked(WorktreeScanResult result, string name) =>
        result.Roots.SelectMany(r => r.Walked).Single(f => f.Facts.Name == name);

    private static FolderFacts Fact(WorktreeScanResult result, string name) =>
        result.Roots.SelectMany(r => r.Facts).Single(f => f.Name == name);

    [Fact]
    public void Every_direct_child_is_classified_and_only_worktrees_and_orphans_are_walked()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("scan-kinds");
        world.Main("main");
        world.Worktree("wt1");
        world.Pointing("gone", "gitdir: C:/no/where/.git/worktrees/gone\n");
        world.Pointing("fx", "gitdir: /home/me/repo/.git/worktrees/fx\n");
        world.PlainFolder("docs");
        world.Sandbox.File("work/a-file.txt", "files in a root are not folders");
        world.Age(world.Work);

        var result = Scan(world);

        result.Roots.Single().Facts.Select(f => (f.Name, f.Kind)).Should().BeEquivalentTo(
            [("main", FolderKind.MainClone), ("wt1", FolderKind.Worktree), ("gone", FolderKind.Orphan), ("fx", FolderKind.ForeignPointer), ("docs", FolderKind.NotGit)]);
        result.Roots.Single().Walked.Select(f => f.Facts.Name).Should().BeEquivalentTo("wt1", "gone");
        result.Complete.Should().BeTrue();
    }

    [Fact]
    public void A_worktree_untouched_for_a_month_is_idle_and_dated_by_its_newest_write()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("scan-idle");
        world.Main("main");
        world.Worktree("wt1");
        world.Age(world.Work);

        var scanned = Walked(Scan(world), "wt1");

        scanned.Idle.Kind.Should().Be(IdleKind.Idle, scanned.Idle.Reason);
        scanned.Idle.Newest.Should().BeCloseTo(world.Now - world.Old, TimeSpan.FromSeconds(2));
        scanned.Bytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public void A_fresh_file_deep_inside_makes_the_worktree_active()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("scan-active");
        world.Main("main");
        world.Worktree("wt1");
        world.Sandbox.File("work/wt1/src/deep/deeper/new.txt", "fresh");
        world.Age(world.Work);
        world.Stamp(world.Sandbox.Under("work/wt1/src/deep/deeper/new.txt"), TimeSpan.FromHours(1));

        Walked(Scan(world), "wt1").Idle.Kind.Should().Be(IdleKind.Active);
    }

    [Theory]
    [InlineData("index")]
    [InlineData("HEAD")]
    [InlineData("logs/HEAD")]
    public void A_fresh_file_in_the_admin_folder_makes_the_worktree_active(string adminFile)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("scan-admin");
        world.Main("main");
        world.Worktree("wt1");
        world.Age(world.Work);
        world.Stamp(Path.Combine(world.AdminOf("wt1"), adminFile.Replace('/', '\\')), TimeSpan.FromHours(1));

        Walked(Scan(world), "wt1").Idle.Kind.Should().Be(IdleKind.Active, "git touches the admin files on every status, add and checkout — agents run them constantly");
    }

    [Fact]
    public void A_future_dated_file_is_never_idle()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("scan-future");
        world.Main("main");
        world.Worktree("wt1");
        world.Age(world.Work);
        world.Stamp(world.Sandbox.Under("work/wt1/src/Program.cs"), TimeSpan.FromDays(-2));

        Walked(Scan(world), "wt1").Idle.Kind.Should().Be(IdleKind.Active, "a clock that disagrees is not evidence of idleness");
    }

    [Fact]
    public void A_fresh_file_inside_build_output_does_not_wake_a_worktree_but_the_build_folders_own_write_does()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("scan-buildtime");
        world.Main("main");
        var folder = world.Worktree("wt1");
        var bin = world.BuildOutput(folder);
        world.Age(world.Work);
        world.Stamp(Path.Combine(bin, "Debug", "net10.0", "App.dll"), TimeSpan.FromHours(1));

        Walked(Scan(world), "wt1").Idle.Kind.Should().Be(IdleKind.Idle, "the inside of a build folder is walked separately: a build today keeps that bin, not the whole worktree");

        world.Stamp(bin, TimeSpan.FromHours(1));

        Walked(Scan(world), "wt1").Idle.Kind.Should().Be(IdleKind.Active, "the build folder's OWN write time counts");
    }

    [Fact]
    public void A_walk_past_its_budget_is_unknown_never_idle()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("scan-budget");
        world.Main("main");
        world.Worktree("wt1");
        for (var i = 0; i < 12; i++)
        {
            world.Sandbox.File($"work/wt1/src/f{i}.cs", "x");
        }

        world.Age(world.Work);

        var result = Scan(world, WorktreeWorld.DefaultSettings() with { MaxEntries = 4 });

        var scanned = Walked(result, "wt1");
        scanned.Idle.Kind.Should().Be(IdleKind.Unknown);
        scanned.Idle.Reason.Should().Contain("entries");
        result.Complete.Should().BeFalse();
    }

    [Fact]
    public void A_scan_past_its_time_budget_is_incomplete_and_names_no_folder_idle()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("scan-time");
        world.Main("main");
        world.Worktree("wt1");
        world.Age(world.Work);

        var result = Scan(world, WorktreeWorld.DefaultSettings() with { MaxSeconds = 0 });

        result.Complete.Should().BeFalse();
        result.Roots.SelectMany(r => r.Walked).Should().NotContain(f => f.Idle.Kind == IdleKind.Idle, "what a scan could not finish is never idle");
    }

    [Fact]
    public async Task An_unreadable_subfolder_makes_the_worktree_unknown_not_idle()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("scan-denied");
        world.Main("main");
        world.Worktree("wt1");
        world.Sandbox.File("work/wt1/src/secret/inside.txt", "may be fresh for all anyone knows");
        world.Age(world.Work);
        await using var denial = await AccessDenial.TryDenyAsync(world.Sandbox.Under("work/wt1/src/secret"));
        Assert.SkipWhen(denial is null, "this account cannot be denied a folder here (elevated)");

        var scanned = Walked(Scan(world), "wt1");

        scanned.Idle.Kind.Should().Be(IdleKind.Unknown, "an unreadable subtree may hold a fresh write");
        scanned.Idle.Reason.Should().Contain("could not be read");
    }

    [Fact]
    public void A_junction_inside_a_worktree_is_never_entered()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("scan-junction");
        world.Main("main");
        world.Worktree("wt1");
        var outside = world.Sandbox.Dir("elsewhere");
        world.Sandbox.File("elsewhere/fresh.bin", new string('f', 5000));
        Assert.SkipUnless(DirectoryLinks.TryCreate(world.Sandbox.Under("work/wt1/linked"), outside), "no junction could be made here");
        world.Age(world.Work);
        world.Stamp(Path.Combine(outside, "fresh.bin"), TimeSpan.FromMinutes(5));

        var scanned = Walked(Scan(world), "wt1");

        scanned.Bytes.Should().BeLessThan(5000, "what the junction leads to is not this worktree's");
        scanned.Idle.Kind.Should().Be(IdleKind.Idle, "a write on the other side of a link is not a write here");
    }

    [Fact]
    public void Bin_without_a_project_file_beside_it_is_not_build_output()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("scan-nobinproject");
        world.Main("main");
        world.Worktree("wt1");
        world.Sandbox.File("work/wt1/scripts/bin/tool.exe", "x");
        world.Sandbox.File("work/wt1/lib/Lib.proj", "<x/>");
        world.Sandbox.File("work/wt1/lib/bin/lib.dll", "x");
        world.Age(world.Work);
        world.Stamp(world.Sandbox.Under("work/wt1/scripts/bin/tool.exe"), TimeSpan.FromHours(1));

        var scanned = Walked(Scan(world), "wt1");

        scanned.Candidates.Should().BeEmpty("a bin beside no project file (a .proj is not in the closed list) is a folder like any other");
        scanned.Idle.Kind.Should().Be(IdleKind.Active, "and what is written in it counts");
    }

    [Fact]
    public void A_bin_and_an_obj_beside_a_project_file_are_build_output_with_their_logical_bytes()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("scan-binobj");
        world.Main("main");
        var folder = world.Worktree("wt1");
        world.BuildOutput(folder);
        world.BuildOutput(Path.Combine(folder, "src", "Lib"), project: "Lib.fsproj", kind: "obj");
        world.Age(world.Work);

        var scanned = Walked(Scan(world), "wt1");

        scanned.Candidates.Select(c => c.Relative).Should().BeEquivalentTo("bin", @"src\Lib\obj");
        scanned.Candidates.Should().OnlyContain(c => c.Retained.Length == 0);
        scanned.Candidates.Single(c => c.Name == "bin").Bytes.Should().Be(1000 + 500 + 2, "App.dll + App.pdb + App.deps.json, logical bytes");
        scanned.Bytes.Should().BeGreaterThan(scanned.Candidates.Sum(c => c.Bytes), "the folder's size holds its build output and the rest");
    }

    [Fact]
    public void Node_modules_without_package_json_is_not_build_output()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("scan-nopackage");
        world.Main("main");
        world.Worktree("wt1");
        world.Sandbox.File("work/wt1/vendor/node_modules/left/index.js", "x");
        world.Age(world.Work);

        Walked(Scan(world), "wt1").Candidates.Should().BeEmpty();
    }

    [Fact]
    public void A_nested_node_modules_is_not_counted_twice()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("scan-nested-modules");
        world.Main("main");
        world.Worktree("wt1");
        world.Sandbox.File("work/wt1/package.json", "{}");
        world.Sandbox.File("work/wt1/node_modules/a/index.js", new string('a', 100));
        world.Sandbox.File("work/wt1/node_modules/a/node_modules/b/index.js", new string('b', 200));
        world.Sandbox.File("work/wt1/node_modules/a/package.json", "{}");
        world.Age(world.Work);

        var candidates = Walked(Scan(world), "wt1").Candidates;

        var modules = candidates.Should().ContainSingle().Subject;
        modules.Relative.Should().Be("node_modules");
        modules.Bytes.Should().Be(100 + 200 + 2, "the nested one is inside the top-most one, counted once");
        modules.Retained.Should().BeEmpty("node_modules is exempt from the content check");
    }

    [Fact]
    public void A_bin_holding_a_nested_repository_is_retained_whole()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("scan-nested-repo");
        world.Main("main");
        var folder = world.Worktree("wt1");
        world.BuildOutput(folder);
        world.Sandbox.File("work/wt1/bin/Debug/vendored/.git/HEAD", "ref: refs/heads/x");
        world.BuildOutput(Path.Combine(folder, "tools"), project: "Tools.csproj");
        world.Sandbox.File("work/wt1/tools/bin/Debug/net10.0/sub/.git", "gitdir: C:/elsewhere");
        world.Age(world.Work);

        var candidates = Walked(Scan(world), "wt1").Candidates;

        candidates.Should().HaveCount(2);
        candidates.Should().OnlyContain(c => c.Retained.Contains("nested repository"), "a .git folder or file anywhere inside retains the candidate WHOLE");
    }

    [Fact]
    public void A_bin_holding_a_sqlite_file_is_retained_and_names_it()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("scan-sqlite");
        world.Main("main");
        var folder = world.Worktree("wt1");
        world.BuildOutput(folder);
        world.Sandbox.File("work/wt1/bin/Debug/net10.0/local.db", "SQLite format 3");
        world.Age(world.Work);

        var candidate = Walked(Scan(world), "wt1").Candidates.Single();

        candidate.Retained.Should().Contain("local.db").And.Contain("not a build artifact");
    }

    [Fact]
    public void A_json_written_after_the_build_retains_its_bin()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("scan-skew");
        world.Main("main");
        var folder = world.Worktree("wt1");
        world.BuildOutput(folder);
        world.Sandbox.File("work/wt1/bin/Debug/net10.0/local-state.json", "{}");
        world.Age(world.Work, TimeSpan.FromDays(30));
        world.Stamp(world.Sandbox.Under("work/wt1/bin/Debug/net10.0/local-state.json"), TimeSpan.FromDays(30) - TimeSpan.FromMinutes(30));

        var candidate = Walked(Scan(world), "wt1").Candidates.Single();

        candidate.Retained.Should().Contain("local-state.json").And.Contain("after the build");
    }

    [Fact]
    public void An_orphans_bin_holding_an_env_file_is_retained()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("scan-orphan-env");
        world.Main("main");
        var folder = world.Pointing("gone", "gitdir: C:/no/where/.git/worktrees/gone\n");
        world.BuildOutput(folder);
        world.Sandbox.File("work/gone/bin/Debug/net10.0/.env", "SECRET=1");
        world.Age(world.Work);

        var scanned = Walked(Scan(world), "gone");

        scanned.Facts.Kind.Should().Be(FolderKind.Orphan);
        scanned.Candidates.Single().Retained.Should().Contain(".env");
    }

    [Fact]
    public void A_candidate_whose_own_walk_did_not_finish_is_retained_with_that_said()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("scan-candidate-budget");
        world.Main("main");
        var folder = world.Worktree("wt1");
        world.BuildOutput(folder);
        for (var i = 0; i < 20; i++)
        {
            world.Sandbox.File($"work/wt1/bin/Debug/net10.0/lib{i}.dll", "x");
        }

        world.Age(world.Work);

        var scanned = Walked(Scan(world, WorktreeWorld.DefaultSettings() with { MaxEntries = 8 }), "wt1");

        scanned.Candidates.Should().OnlyContain(c => c.Retained.Contains("did not finish"));
    }

    [Fact]
    public async Task A_denied_entry_inside_a_candidate_retains_it()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("scan-candidate-denied");
        world.Main("main");
        var folder = world.Worktree("wt1");
        world.BuildOutput(folder);
        world.Sandbox.File("work/wt1/bin/Debug/closed/x.dll", "x");
        world.Age(world.Work);
        await using var denial = await AccessDenial.TryDenyAsync(world.Sandbox.Under("work/wt1/bin/Debug/closed"));
        Assert.SkipWhen(denial is null, "this account cannot be denied a folder here (elevated)");

        Walked(Scan(world), "wt1").Candidates.Single().Retained.Should().Contain("could not be read");
    }

    [Fact]
    public void A_root_set_by_hand_that_breaks_a_rule_is_refused_at_scan_start()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("scan-bad-root");
        world.Main("main");
        world.Worktree("wt1");
        world.Age(world.Work);

        var result = Scan(world, null, world.Sandbox.Path, world.Work, world.Sandbox.Under("gone"));

        result.Roots.Should().HaveCount(3);
        result.Roots[0].Refused.Should().Contain("profile", "the sandbox holds the profile");
        result.Roots[0].Walked.Should().BeEmpty();
        result.Roots[1].Refused.Should().BeEmpty();
        result.Roots[1].Walked.Should().ContainSingle(f => f.Facts.Name == "wt1");
        result.Roots[2].Refused.Should().Contain("exist");
    }

    [Fact]
    public void A_root_on_a_drive_that_is_no_longer_local_fixed_is_refused_at_scan_start()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("scan-drive");
        world.Main("main");

        var result = world.Scanner(drives: _ => DriveType.Network).Run([world.Work], WorktreeWorld.DefaultSettings(), CancellationToken.None);

        result.Roots.Single().Refused.Should().Contain("drive");
        result.Roots.Single().Facts.Should().BeEmpty();
    }

    [Fact]
    public void A_scan_deletes_changes_and_creates_nothing_in_the_roots()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("scan-readonly");
        world.Main("main");
        var folder = world.Worktree("wt1");
        world.BuildOutput(folder);
        world.Age(world.Work);
        var before = Snapshot(world.Work);

        _ = Scan(world);

        Snapshot(world.Work).Should().Equal(before, "a scan only reads");
    }

    private static IReadOnlyList<string> Snapshot(string root) =>
        [.. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(p => $"{p}|{(Directory.Exists(p) ? 0 : new FileInfo(p).Length)}|{File.GetLastWriteTimeUtc(p).Ticks}")];
}
