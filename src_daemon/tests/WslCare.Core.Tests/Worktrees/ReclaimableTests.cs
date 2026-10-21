using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Thresholds;
using WslCare.Core.Worktrees;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Worktrees;

/// <summary>
/// E19.S4 (PLAN_disk_hygiene_windows.md §3.3 "Reclaimable means only what a cleanup would actually be allowed to delete"): the scan runs git's
/// three questions for the candidates that passed every structural check in an eligible folder, and the report counts ONLY what passes every
/// check — as <c>a24</c> (worktrees) and <c>a24Orphans</c> (orphans, ignore-confirmed) — and names the rest by reason. A scripted
/// <c>git.exe</c> answers by the folder it was started in, through the PRODUCT policy; the real one runs in <c>GitQuestionsRealGitTests</c>.
/// </summary>
public sealed class ReclaimableTests
{
    private static readonly GitLimits Limits = new(TimeSpan.FromSeconds(30), 64, 7000);

    /// <summary>What the scripted git says per folder name: the names ignored and the tracked paths; a folder named nowhere is ignored nothing.</summary>
    private sealed class ScriptedGit(IReadOnlyDictionary<string, string[]> ignored, IReadOnlyDictionary<string, string[]> tracked)
    {
        public RecordingCommandRunner Runner { get; } = new RecordingCommandRunner { Policy = CommandPolicy.Product };

        /// <summary>How long every <c>rev-parse</c> takes: a slow git, for the scan's time budget.</summary>
        public TimeSpan Sleep { get; init; }

        public GitQuestions Questions(Func<string, bool>? exists = null)
        {
            Runner.ScriptEffect(argv => argv.Contains("rev-parse"), request =>
            {
                Thread.Sleep(Sleep);
                return RecordingCommandRunner.Exited(0, request.WorkingDirectory.Replace('\\', '/') + "\n");
            });
            Runner.ScriptEffect(argv => argv.Contains("ls-files"), request => RecordingCommandRunner.Exited(0, string.Concat(Of(tracked, request).Select(t => t + "\0"))));
            Runner.ScriptEffect(argv => argv.Contains("check-ignore"), request => Check(request));
            return new GitQuestions(Runner, Limits, _ => string.Empty, exists ?? Directory.Exists);
        }

        private static string[] Of(IReadOnlyDictionary<string, string[]> map, CommandRequest request) =>
            map.GetValueOrDefault(Path.GetFileName(request.WorkingDirectory.TrimEnd('\\')), []);

        private CommandOutcome Check(CommandRequest request)
        {
            var folder = Path.GetFileName(request.WorkingDirectory.TrimEnd('\\'));
            var names = request.Argv.SkipWhile(a => a != "--").Skip(1).ToList();
            var yes = ignored.GetValueOrDefault(folder, []);
            var lines = names.Select(n => yes.Contains(n, StringComparer.OrdinalIgnoreCase) ? $".gitignore:1:{n}\t{n}" : $"::\t{n}");
            return RecordingCommandRunner.Exited(names.Any(n => yes.Contains(n, StringComparer.OrdinalIgnoreCase)) ? 0 : 1, string.Join('\n', lines) + "\n");
        }
    }

    private static WorktreeScanResult Scan(WorktreeWorld world, ScriptedGit? git) =>
        world.Scanner(git: git?.Questions()).Run([world.Work], WorktreeWorld.DefaultSettings(), CancellationToken.None);

    private static IReadOnlyDictionary<string, string[]> Map(params (string Folder, string[] Names)[] entries) => entries.ToDictionary(e => e.Folder, e => e.Names, StringComparer.OrdinalIgnoreCase);

    /// <summary>A world with one candidate of each kind of fate (bin = 1 500 bytes, plus 12 of the project file outside it).</summary>
    private static WorktreeWorld Fates(string purpose)
    {
        var world = new WorktreeWorld(purpose);
        world.Main("main");
        foreach (var name in new[] { "wt-ok", "wt-tracked", "wt-locked", "wt-recent", "wt-notignored", "wt-structural" })
        {
            world.Worktree(name);
            world.BuildOutput(Path.Combine(world.Work, name));
        }

        world.Sandbox.File("work/wt-structural/bin/Debug/net10.0/cache.db", "x");
        world.Sandbox.File("work/main/.git/worktrees/wt-locked/locked", "in use\n");
        world.Pointing("orph", "gitdir: " + WorktreeWorld.Forward(world.AdminOf("orph")) + "\n");
        world.BuildOutput(Path.Combine(world.Work, "orph"));
        world.Pointing("orph-lost", "gitdir: C:/no/where/else/.git/worktrees/orph-lost\n");
        world.BuildOutput(Path.Combine(world.Work, "orph-lost"));
        foreach (var old in new[] { "main", "wt-ok", "wt-tracked", "wt-locked", "wt-notignored", "wt-structural", "orph", "orph-lost" })
        {
            world.Age(Path.Combine(world.Work, old));
        }

        return world;
    }

    private static ScriptedGit TheScript() => new(
        Map(("wt-ok", ["bin"]), ("wt-tracked", ["bin"]), ("wt-locked", ["bin"]), ("wt-recent", ["bin"]), ("wt-structural", ["bin"]), ("orph", ["bin"]), ("orph-lost", ["bin"])),
        Map(("wt-tracked", ["bin/Debug/net10.0/App.dll"])));

    private static long BinBytes => 1000 + 500 + 2;

    [Fact]
    public void Reclaimable_counts_only_candidates_that_pass_every_check_and_names_the_rest()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = Fates("reclaim");
        var git = TheScript();

        var report = WorktreeReports.From(Scan(world, git), WorktreeWorld.DefaultSettings());

        var reclaimable = report.Reclaimable;
        reclaimable.Available.Should().BeTrue();
        reclaimable.A24.Should().Be(new Tally(1, BinBytes), "only wt-ok passes everything: idle, unlocked, structurally clean, ignored, nothing tracked");
        reclaimable.A24Orphans.Should().Be(new Tally(1, BinBytes), "orph's repository is still there and its ignore rules confirm it; orph-lost's repository is not");
        reclaimable.Retained!.ToDictionary(r => r.Reason, r => r.Count).Should().BeEquivalentTo(new Dictionary<string, int>
        {
            [WorktreeReports.RecentReason] = 1,
            [WorktreeReports.LockedReason] = 1,
            [GitReasons.Tracked] = 1,
            [GitReasons.NotIgnored] = 1,
            [GitReasons.OrphanNotConfirmed] = 1,
            ["holds a file that is not a build artifact"] = 1,
        });
        var all = report.Roots.Single().BuildOutput.All;
        (reclaimable.ConfirmedBytes + reclaimable.Retained!.Sum(r => r.Bytes)).Should().Be(all.Bytes, "every candidate lands in exactly one place: nothing counted twice, nothing dropped");
        (reclaimable.A24!.Count + reclaimable.A24Orphans!.Count + reclaimable.Retained!.Sum(r => r.Count)).Should().Be(all.Count);
    }

    [Fact]
    public void Git_is_asked_only_for_candidates_that_passed_every_check_in_an_eligible_folder()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = Fates("reclaim-asked");
        var git = TheScript();

        Scan(world, git);

        var asked = git.Runner.Requests.Where(r => !r.Argv.Contains("--version")).Select(r => Path.GetFileName(r.WorkingDirectory.TrimEnd('\\'))).Distinct().ToList();
        asked.Should().BeEquivalentTo("wt-ok", "wt-tracked", "wt-notignored", "orph");
        asked.Should().NotContain(["wt-locked", "wt-recent", "wt-structural"], "another check already keeps these, and the first reason is the one reported");
        asked.Should().NotContain("orph-lost", "its repository is gone: nothing is started, it is not confirmed by git");
        git.Runner.Requests.Where(r => r.Argv.Contains("--no-index")).Select(r => Path.GetFileName(r.WorkingDirectory.TrimEnd('\\'))).Should().BeEquivalentTo("orph");
    }

    [Fact]
    public void Every_git_command_the_scan_starts_is_one_of_the_three_read_only_templates()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = Fates("reclaim-templates");
        var git = TheScript();

        Scan(world, git);

        git.Runner.Requests.Should().NotBeEmpty();
        git.Runner.Requests.Select(r => CommandCatalogue.Product.MatchMachine(r.Argv)?.Name).Should().OnlyContain(n => n == "git-version" || n == "git-toplevel" || n == "git-ls-files" || n == "git-check-ignore" || n == "git-check-ignore-orphan");
        git.Runner.Requests.Where(r => !r.Argv.Contains("--version")).Should().OnlyContain(r => r.Environment is CommandEnvironment.Clean && r.WorkingDirectory.StartsWith(world.Work, StringComparison.OrdinalIgnoreCase));
        git.Runner.Requests.Count(r => r.Argv.Contains("--version")).Should().Be(1, "git is probed once per scan");
    }

    [Fact]
    public void A_scan_with_no_git_to_ask_says_so_and_is_never_zero()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = Fates("reclaim-nogit");

        var report = WorktreeReports.From(Scan(world, git: null), WorktreeWorld.DefaultSettings());

        report.Reclaimable.Available.Should().BeFalse();
        report.Reclaimable.Reason.Should().Contain("git has not confirmed");
        report.Reclaimable.A24.Should().BeNull("absent is not zero");
    }

    [Fact]
    public void No_admitted_git_is_a_named_state_not_an_empty_reclaimable_list()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = Fates("reclaim-gitgone");
        const string why = "git.exe is not started: no admitted git (it is never looked up on PATH)";
        var runner = new RecordingCommandRunner { Default = new CommandOutcome.FailedToStart(why), Policy = CommandPolicy.Product };
        var questions = new GitQuestions(runner, Limits, _ => string.Empty, Directory.Exists);

        var report = WorktreeReports.From(world.Scanner(git: questions).Run([world.Work], WorktreeWorld.DefaultSettings(), CancellationToken.None), WorktreeWorld.DefaultSettings());

        var reclaimable = report.Reclaimable;
        reclaimable.Available.Should().BeFalse("nothing was confirmed because git could not be asked: absent, not zero");
        reclaimable.A24.Should().BeNull();
        reclaimable.A24Orphans.Should().BeNull();
        reclaimable.GitUnavailable.Should().Contain(why);
        reclaimable.Reason.Should().StartWith("git could not be asked").And.Contain(why);
        reclaimable.Retained!.Should().Contain(r => r.Reason == GitReasons.Unavailable && r.Count >= 4, "every candidate that needed git carries the reason");
        reclaimable.NotAsked.Should().BeGreaterThan(0);

        var status = WorktreesStatus.From([world.Work], Reading.Of(report), WorktreeWorld.DefaultSettings(), DateTimeOffset.UtcNow);

        status.Verdict.Level.Should().Be(Level.Unknown, "never ok, never zero");
        status.Verdict.Reason.Should().Contain("git could not be asked").And.Contain(why);
        status.Section.Report!.Reclaimable.GitUnavailable.Should().Contain(why, "the scan fact reaches status");
    }

    [Fact]
    public void A_scan_whose_time_ran_out_does_not_ask_git_and_says_the_candidates_were_not_asked()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = Fates("reclaim-budget");
        var git = TheScript();

        var result = world.Scanner(git: git.Questions()).Run([world.Work], WorktreeWorld.DefaultSettings() with { MaxSeconds = 0 }, CancellationToken.None);

        git.Runner.Requests.Select(r => r.Argv).Should().OnlyContain(argv => argv.Contains("--version"), "a scan past its time budget asks no question; only the probe of whether git can be run at all ran");
        result.Complete.Should().BeFalse();
    }

    [Fact]
    public void A_policy_refusal_for_one_orphan_retains_only_that_folders_candidates_and_the_rest_are_still_confirmed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("reclaim-refusal");
        world.Main("main");
        world.Worktree("wt-ok");
        world.BuildOutput(Path.Combine(world.Work, "wt-ok"));
        // An orphan whose repository lies under <home>\git: the never-list's protected-path rule refuses the --git-dir argument.
        world.Pointing("orph-home", "gitdir: C:/Users/me/git/repo/.git/worktrees/orph-home\n");
        world.BuildOutput(Path.Combine(world.Work, "orph-home"));
        foreach (var old in new[] { "main", "wt-ok", "orph-home" })
        {
            world.Age(Path.Combine(world.Work, old));
        }

        var git = new ScriptedGit(Map(("wt-ok", ["bin"]), ("orph-home", ["bin"])), Map());

        var report = WorktreeReports.From(world.Scanner(git: git.Questions(exists: _ => true)).Run([world.Work], WorktreeWorld.DefaultSettings(), CancellationToken.None), WorktreeWorld.DefaultSettings());

        var reclaimable = report.Reclaimable;
        reclaimable.Available.Should().BeTrue("the refusal is that folder's, not git's absence");
        reclaimable.GitUnavailable.Should().BeNull();
        reclaimable.A24.Should().Be(new Tally(1, BinBytes), "the normal worktree's bin is still confirmed");
        reclaimable.A24Orphans.Should().Be(new Tally(0, 0));
        reclaimable.Retained!.Should().ContainSingle(r => r.Reason == GitReasons.PolicyRefusal && r.Count == 1);
        reclaimable.NotAsked.Should().Be(1, "a candidate the policy refused is not a confirmed zero");
    }

    [Fact]
    public void A_scan_whose_git_cannot_be_run_says_so_even_when_no_candidate_needed_a_question()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("reclaim-probe-gone");
        world.Main("main");
        world.Worktree("wt-recent");
        world.BuildOutput(Path.Combine(world.Work, "wt-recent"));
        const string why = "git.exe is not started: no admitted git (it is never looked up on PATH)";
        var runner = new RecordingCommandRunner { Default = new CommandOutcome.FailedToStart(why), Policy = CommandPolicy.Product };
        var questions = new GitQuestions(runner, Limits, _ => string.Empty, Directory.Exists);

        var report = WorktreeReports.From(world.Scanner(git: questions).Run([world.Work], WorktreeWorld.DefaultSettings(), CancellationToken.None), WorktreeWorld.DefaultSettings());

        report.Reclaimable.Available.Should().BeFalse();
        report.Reclaimable.GitUnavailable.Should().Contain(why);
        runner.Requests.Should().ContainSingle("only the probe ran: no candidate needed a question");
        var verdict = WorktreesStatus.From([world.Work], Reading.Of(report), WorktreeWorld.DefaultSettings(), DateTimeOffset.UtcNow).Verdict;
        verdict.Level.Should().Be(Level.Unknown, "never ok when git cannot be asked");
        verdict.Reason.Should().Contain("git could not be asked");
    }

    [Fact]
    public void A_scan_where_no_candidate_needed_git_says_so_instead_of_implying_git_confirmed_it()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("reclaim-nothing-needed");
        world.Main("main");
        world.Worktree("wt-recent");
        world.BuildOutput(Path.Combine(world.Work, "wt-recent"));
        var git = new ScriptedGit(Map(), Map());

        var report = WorktreeReports.From(world.Scanner(git: git.Questions()).Run([world.Work], WorktreeWorld.DefaultSettings(), CancellationToken.None), WorktreeWorld.DefaultSettings());

        report.Reclaimable.Available.Should().BeTrue();
        report.Reclaimable.NothingNeededGit.Should().BeTrue();
        report.Reclaimable.A24.Should().Be(new Tally(0, 0));
        var verdict = WorktreesStatus.From([world.Work], Reading.Of(report), WorktreeWorld.DefaultSettings(), DateTimeOffset.UtcNow).Verdict;
        verdict.Reason.Should().Contain("nothing needed git");
        git.Runner.Requests.Should().ContainSingle(r => r.Argv.Contains("--version"), "the probe ran once; no question did");
        git.Runner.Requests.Should().HaveCount(1);
    }

    [Fact]
    public void The_scans_time_budget_bounds_the_git_time_and_later_folders_are_not_asked()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("reclaim-gitbudget");
        world.Main("main");
        var names = new[] { "wt-a", "wt-b", "wt-c", "wt-d" };
        foreach (var name in names)
        {
            world.Worktree(name);
            world.BuildOutput(Path.Combine(world.Work, name));
            world.Age(Path.Combine(world.Work, name));
        }

        world.Age(Path.Combine(world.Work, "main"));
        var git = new ScriptedGit(Map(names.Select(n => (n, new[] { "bin" })).ToArray()), Map()) { Sleep = TimeSpan.FromMilliseconds(1500) };
        var settings = WorktreeWorld.DefaultSettings() with { MaxSeconds = 2 };

        var result = world.Scanner(git: git.Questions()).Run([world.Work], settings, CancellationToken.None);

        var asked = git.Runner.Requests.Where(r => r.Argv.Contains("rev-parse")).Select(r => Path.GetFileName(r.WorkingDirectory.TrimEnd('\\'))).Distinct().ToList();
        asked.Count.Should().BeLessThan(names.Length, "once the scan's time is spent no further folder is asked");
        git.Runner.Requests.Where(r => r.Argv.Contains("rev-parse")).Should().OnlyContain(r => r.Timeout <= TimeSpan.FromSeconds(2), "no call may outlive the scan's remaining time");
        result.Complete.Should().BeFalse("a scan that ran out of time is incomplete");
        asked.Should().NotContain(names[^1], "the last folder is reached only after the time is spent, and git is not started for it");
    }

    [Fact]
    public void The_orphans_repository_is_the_folder_two_above_its_admin_folder_and_nothing_else()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("reclaim-orphan-shape");
        var scanner = world.Scanner();

        scanner.OrphanRepository(@"D:\w\main\.git\worktrees\x").Should().Be(@"D:\w\main\.git");
        scanner.OrphanRepository(@"D:\w\main\.git\WORKTREES\x").Should().Be(@"D:\w\main\.git");
        scanner.OrphanRepository(@"D:\w\main\.git\modules\x").Should().BeEmpty();
        scanner.OrphanRepository(@"D:\somewhere\x").Should().BeEmpty();
        scanner.OrphanRepository(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void A_scan_over_a_real_linked_worktree_confirms_its_ignored_bin_end_to_end()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() && RealGit.Available, "the real git.exe of Git for Windows: the Windows legs");
        using var world = new WorktreeWorld("reclaim-real");
        var isolated = new Dictionary<string, string> { ["GIT_CONFIG_GLOBAL"] = "NUL", ["GIT_CONFIG_NOSYSTEM"] = "1" };
        var main = Path.Combine(world.Work, "main");
        var worktree = Path.Combine(world.Work, "wt1");
        RealGit.Run(world.Work, ["init", "-q", "main"], isolated).ExitCode.Should().Be(0);
        // `worktree add --orphan` makes a linked worktree on an unborn branch: no commit, so no identity is needed or set anywhere.
        var added = RealGit.Run(main, ["worktree", "add", "--orphan", "-b", "feature", "../wt1"], isolated);
        added.ExitCode.Should().Be(0, added.Stderr);
        world.Sandbox.File("work/wt1/.gitignore", "bin/\nobj/\n");
        world.Sandbox.File("work/wt1/src/Program.cs", "class P {}\n");
        world.BuildOutput(worktree);
        RealGit.Run(worktree, ["add", "--", ".gitignore", "src/Program.cs", "App.csproj"], isolated).ExitCode.Should().Be(0);
        world.Age(worktree);
        world.Age(world.AdminOf("wt1"));
        var questions = new GitQuestions(new ProcessCommandRunner(CommandPolicy.Product), Limits, name => GitEnvironment.ThisProcess(name), Directory.Exists);

        var result = world.Scanner(git: questions).Run([world.Work], WorktreeWorld.DefaultSettings(), CancellationToken.None);

        var facts = result.Roots.Single().Facts.Single(f => f.Name == "wt1");
        facts.Kind.Should().Be(FolderKind.Worktree, facts.Why);
        var walked = result.Roots.Single().Walked.Single(f => f.Facts.Name == "wt1");
        walked.Idle.Kind.Should().Be(IdleKind.Idle, walked.Idle.Reason);
        var bin = walked.Candidates.Single(c => c.Relative == "bin");
        bin.Retained.Should().BeEmpty();
        bin.Git.Should().Be(new CandidateGit.Confirmed(TrackedFilesChecked: true));
        var report = WorktreeReports.From(result, WorktreeWorld.DefaultSettings());
        report.Reclaimable.A24.Should().Be(new Tally(1, BinBytes), "the ignored, untracked bin of an idle linked worktree is what a cleanup may delete");
    }

    private static ReclaimableReport Confirmed(long a24, long orphans, params RetainedGroup[] kept) =>
        new(true, string.Empty) { A24 = new Tally(a24 > 0 ? 3 : 0, a24), A24Orphans = new Tally(orphans > 0 ? 1 : 0, orphans), Retained = kept };

    private static WorktreeReport ReportWith(ReclaimableReport reclaimable, bool complete = true, DateTimeOffset? at = null) =>
        new(Core.SchemaVersion.Current, at ?? Now.AddHours(-1), 3, complete, [], [], reclaimable);

    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private const long Gib = 1L << 30;

    private static Verdict VerdictOf(WorktreeReport report) =>
        WorktreesStatus.From([@"D:\w"], Reading.Of(report), WorktreeWorld.DefaultSettings(), Now).Verdict;

    [Fact]
    public void The_verdict_warns_above_the_limit_and_the_value_names_both_parts()
    {
        var verdict = VerdictOf(ReportWith(Confirmed(15 * Gib, 6 * Gib, new RetainedGroup(GitReasons.NotIgnored, 2, 100))));

        verdict.Level.Should().Be(Level.Warn);
        verdict.Value.Should().StartWith("21.0 GiB: 15.0 GiB in 3 worktree folders + 6.0 GiB in 1 orphan folder");
        verdict.Reason.Should().Contain("not ignored by git (2)");
        verdict.Limit.Should().Contain("20 GiB");
        verdict.Basis!.Source.Should().Be(VerdictSource.Scan);
    }

    [Theory]
    [InlineData(20, Level.Ok)]
    [InlineData(1, Level.Ok)]
    [InlineData(21, Level.Warn)]
    public void The_verdict_is_ok_at_or_below_the_limit_and_warns_only_above_it(int gib, Level expected)
    {
        VerdictOf(ReportWith(Confirmed(gib * Gib, 0))).Level.Should().Be(expected);
    }

    [Fact]
    public void A_figure_below_the_limit_is_unknown_not_ok_when_the_scan_did_not_finish_or_git_could_not_be_asked()
    {
        var incomplete = VerdictOf(ReportWith(Confirmed(1 * Gib, 0), complete: false));
        var unasked = VerdictOf(ReportWith(Confirmed(1 * Gib, 0) with { NotAsked = 4 }));
        var warnsAnyway = VerdictOf(ReportWith(Confirmed(30 * Gib, 0), complete: false));

        incomplete.Level.Should().Be(Level.Unknown);
        incomplete.Reason.Should().Contain("lower bound");
        unasked.Level.Should().Be(Level.Unknown);
        unasked.Reason.Should().Contain("4 candidates could not be asked of git");
        warnsAnyway.Level.Should().Be(Level.Warn, "a lower bound already above the limit is still above it");
    }

    [Fact]
    public void A_stale_scan_is_unknown_even_when_it_confirmed_a_figure_above_the_limit()
    {
        var verdict = VerdictOf(ReportWith(Confirmed(30 * Gib, 0), at: Now.AddDays(-9)));

        verdict.Level.Should().Be(Level.Unknown);
        verdict.Reason.Should().Contain("9 days old");
    }

    [Fact]
    public void A_scan_git_never_answered_stays_unknown_with_the_reason()
    {
        var verdict = VerdictOf(ReportWith(new ReclaimableReport(false, WorktreeReports.GitHasNotConfirmed)));

        verdict.Level.Should().Be(Level.Unknown);
        verdict.Reason.Should().Contain("git has not confirmed");
    }

    [Fact]
    public void The_reasons_git_gave_are_counted_under_a_closed_set_of_sentences()
    {
        GitReasons.GroupOf("git refused: fatal: detected dubious ownership in repository at 'D:/x'").Should().Be("git refused the question");
        GitReasons.GroupOf("git did not answer within 30 s").Should().Be("git did not answer in time");
        GitReasons.GroupOf("git could not be run: git.exe is not started").Should().Be("git unavailable");
        GitReasons.IsGitUnavailable("git could not be run: x").Should().BeTrue();
        GitReasons.UnavailableWhy("git could not be run: git.exe is not started").Should().Be("git.exe is not started");
        GitReasons.GroupOf(GitReasons.Tracked).Should().Be(GitReasons.Tracked);
        GitReasons.IsAFailureToAsk("git refused: x").Should().BeTrue();
        GitReasons.IsAFailureToAsk(GitReasons.NotIgnored).Should().BeFalse();
        GitReasons.IsAFailureToAsk(GitReasons.Tracked).Should().BeFalse();
    }
}
