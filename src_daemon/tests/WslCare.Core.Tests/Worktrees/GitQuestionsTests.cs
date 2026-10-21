using FluentAssertions;

using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Worktrees;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Worktrees;

/// <summary>
/// E19.S4: git's three read-only questions, answered by a scripted <c>git.exe</c> that prints what the real one printed in the probe of
/// 2026-10-15 (git 2.55.0.windows.3). Every request goes through the PRODUCT policy, so a command the policy would refuse shows up here as
/// a retained candidate; the real <c>git.exe</c> runs in <c>GitQuestionsRealGitTests</c>.
/// </summary>
public sealed class GitQuestionsTests
{
    private const string Root = @"D:\rsd\_wt";
    private const string Folder = @"D:\rsd\_wt\app";
    private static readonly GitLimits Limits = new(TimeSpan.FromSeconds(30), 64, 7000);

    /// <summary>A scripted repository: what is ignored, what is tracked, and how git answers each of the three commands.</summary>
    private sealed class FakeRepository
    {
        public string TopLevel { get; init; } = "D:/rsd/_wt/app";

        public HashSet<string> Ignored { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Negated { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> Tracked { get; init; } = [];

        public CommandOutcome? RevParse { get; init; }

        public CommandOutcome? LsFiles { get; init; }

        public CommandOutcome? CheckIgnore { get; init; }

        public RecordingCommandRunner Runner() => new RecordingCommandRunner { Policy = CommandPolicy.Product }
            .ScriptEffect(argv => argv.Contains("rev-parse"), _ => RevParse ?? RecordingCommandRunner.Exited(0, TopLevel + "\n"))
            .ScriptEffect(argv => argv.Contains("ls-files"), request => LsFiles ?? RecordingCommandRunner.Exited(0, TrackedUnder(Names(request.Argv))))
            .ScriptEffect(argv => argv.Contains("check-ignore"), request => CheckIgnore ?? Decide(Names(request.Argv)));

        private static IReadOnlyList<string> Names(IReadOnlyList<string> argv) => [.. argv.SkipWhile(a => a != "--").Skip(1)];

        private string TrackedUnder(IReadOnlyList<string> names) =>
            string.Concat(Tracked.Where(t => names.Any(n => t.StartsWith(n.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))).Select(t => t + "\0"));

        private CommandOutcome Decide(IReadOnlyList<string> names)
        {
            var lines = names.Select(n => Negated.Contains(n) ? $".gitignore:2:!{n}\t{n}" : Ignored.Contains(n) ? $".gitignore:1:{n}\t{n}" : $"::\t{n}");
            return RecordingCommandRunner.Exited(names.Any(Ignored.Contains) ? 0 : 1, string.Join('\n', lines) + "\n");
        }
    }

    private static GitQuestions Asker(RecordingCommandRunner runner, GitLimits? limits = null, Func<string, string>? parent = null, Func<string, bool>? exists = null) =>
        new(runner, limits ?? Limits, parent ?? (_ => string.Empty), exists ?? (_ => true));

    private static GitQuestion Repo(params string[] names) => new(Folder, names, Root, new GitFolder.Repository(Folder));

    private static GitVerdict Of(IReadOnlyList<GitAnswer> answers, string name) => answers.Single(a => a.Name == name).Verdict;

    private static GitVerdict.Retained Retained(string reason) => new(reason);

    private static GitVerdict.Confirmed Confirmed => new(TrackedFilesChecked: true);

    [Fact]
    public async Task An_ignored_untracked_folder_in_the_expected_repository_is_confirmed()
    {
        var repository = new FakeRepository { Ignored = { "bin", "obj/" } };

        var answers = await Asker(repository.Runner()).AskAsync(Repo("bin", "obj/"), CancellationToken.None);

        answers.Select(a => a.Name).Should().Equal("bin", "obj/");
        answers.Should().OnlyContain(a => a.Verdict is GitVerdict.Confirmed);
    }

    [Fact]
    public async Task Check_ignore_exit_1_is_an_answer_and_128_a_refusal()
    {
        var none = new FakeRepository();
        var refusing = new FakeRepository { CheckIgnore = RecordingCommandRunner.Exited(128, stderr: "fatal: detected dubious ownership in repository at 'D:/rsd/_wt/app'\nTo add an exception...\n") };

        var exit1 = await Asker(none.Runner()).AskAsync(Repo("bin"), CancellationToken.None);
        var exit128 = await Asker(refusing.Runner()).AskAsync(Repo("bin"), CancellationToken.None);

        Of(exit1, "bin").Should().Be(Retained(GitReasons.NotIgnored), "exit 1 says 'none ignored' — an answer, read per line");
        Of(exit128, "bin").Should().BeOfType<GitVerdict.Retained>().Which.Reason.Should().StartWith("git refused: fatal: detected dubious ownership").And.NotContain("\n");
    }

    [Fact]
    public async Task A_batch_is_judged_per_path_not_by_the_exit_code()
    {
        var repository = new FakeRepository { Ignored = { "a/bin", "c/bin" } };

        var answers = await Asker(repository.Runner()).AskAsync(Repo("a/bin", "b/bin", "c/bin"), CancellationToken.None);

        Of(answers, "a/bin").Should().Be(Confirmed);
        Of(answers, "b/bin").Should().Be(Retained(GitReasons.NotIgnored), "exit 0 means 'at least one is ignored', not 'this one'");
        Of(answers, "c/bin").Should().Be(Confirmed);
    }

    [Fact]
    public async Task An_exit_0_whose_every_line_says_not_ignored_confirms_nothing()
    {
        var repository = new FakeRepository { CheckIgnore = RecordingCommandRunner.Exited(0, "::\tbin\n::\tobj\n") };

        var answers = await Asker(repository.Runner()).AskAsync(Repo("bin", "obj"), CancellationToken.None);

        answers.Should().OnlyContain(a => a.Verdict.Equals(Retained(GitReasons.NotIgnored)));
    }

    [Theory]
    [InlineData("::\tbin\n")]
    [InlineData(".gitignore:4:!bin\tbin\n")]
    public async Task A_negated_ignore_rule_retains(string line)
    {
        var repository = new FakeRepository { CheckIgnore = RecordingCommandRunner.Exited(1, line) };

        var answers = await Asker(repository.Runner()).AskAsync(Repo("bin"), CancellationToken.None);

        Of(answers, "bin").Should().Be(Retained(GitReasons.NotIgnored));
    }

    [Fact]
    public async Task A_tracked_file_inside_bin_retains_the_candidate()
    {
        var repository = new FakeRepository { Ignored = { "bin", "obj" }, Tracked = ["bin/keep.dll", "binary/other.txt"] };

        var answers = await Asker(repository.Runner()).AskAsync(Repo("bin", "obj", "binary"), CancellationToken.None);

        Of(answers, "bin").Should().Be(Retained(GitReasons.Tracked), "a tracked file is inside it, ignore rule or not");
        Of(answers, "obj").Should().Be(Confirmed);
        Of(answers, "binary").Should().Be(Retained(GitReasons.Tracked));
    }

    [Fact]
    public async Task A_tracked_file_wins_over_a_missing_ignore_rule_in_the_reason()
    {
        var repository = new FakeRepository { Tracked = ["bin/keep.dll"] };

        var answers = await Asker(repository.Runner()).AskAsync(Repo("bin"), CancellationToken.None);

        Of(answers, "bin").Should().Be(Retained(GitReasons.Tracked));
    }

    [Fact]
    public async Task A_build_folder_git_does_not_ignore_is_retained()
    {
        var repository = new FakeRepository { Ignored = { "obj" } };

        var answers = await Asker(repository.Runner()).AskAsync(Repo("bin", "obj"), CancellationToken.None);

        Of(answers, "bin").Should().Be(Retained(GitReasons.NotIgnored));
        Of(answers, "obj").Should().Be(Confirmed);
    }

    [Fact]
    public async Task Git_discovery_that_escapes_the_root_retains_every_candidate()
    {
        var repository = new FakeRepository { TopLevel = "D:/rsd" };
        var runner = repository.Runner();

        var answers = await Asker(runner).AskAsync(Repo("bin", "obj"), CancellationToken.None);

        answers.Should().OnlyContain(a => a.Verdict.Equals(Retained(GitReasons.AnotherRepository)));
        runner.Commands.Should().ContainSingle("only rev-parse ran: nothing is asked of another repository").Which.Should().Contain("rev-parse");
    }

    [Theory]
    [InlineData("d:/RSD/_WT/app")]
    [InlineData("D:/rsd/_wt/app/")]
    [InlineData("D:\\rsd\\_wt\\app")]
    public async Task The_top_level_is_compared_by_folder_not_by_spelling(string printed)
    {
        var repository = new FakeRepository { TopLevel = printed, Ignored = { "bin" } };

        var answers = await Asker(repository.Runner()).AskAsync(Repo("bin"), CancellationToken.None);

        Of(answers, "bin").Should().Be(Confirmed);
    }

    [Theory]
    [InlineData(@"D:\rsd\other")]
    [InlineData(@"D:\rsd\_wt\..\other")]
    [InlineData(@"D:\rsd\_wtx\app")]
    [InlineData(@"C:\rsd\_wt\app")]
    [InlineData(@"\\server\share\app")]
    [InlineData(@"app")]
    public async Task A_folder_outside_the_configured_root_is_never_asked_about(string folder)
    {
        var runner = new FakeRepository().Runner();

        var answers = await Asker(runner).AskAsync(new GitQuestion(folder, ["bin"], Root, new GitFolder.Repository(folder)), CancellationToken.None);

        Of(answers, "bin").Should().Be(Retained(GitReasons.OutsideRoot));
        runner.Requests.Should().BeEmpty("no process starts in a folder this code has not placed beneath a root");
    }

    [Fact]
    public async Task The_root_itself_counts_as_beneath_the_root()
    {
        var repository = new FakeRepository { TopLevel = "D:/rsd/_wt", Ignored = { "bin" } };

        var answers = await Asker(repository.Runner()).AskAsync(new GitQuestion(Root, ["bin"], Root, new GitFolder.Repository(Root)), CancellationToken.None);

        Of(answers, "bin").Should().Be(Confirmed);
    }

    [Fact]
    public async Task A_git_refusal_retains_the_candidate_by_name()
    {
        var repository = new FakeRepository { RevParse = RecordingCommandRunner.Exited(128, stderr: "fatal: not a git repository: (NULL)\n") };

        var answers = await Asker(repository.Runner()).AskAsync(Repo("bin", "obj"), CancellationToken.None);

        answers.Select(a => a.Name).Should().Equal("bin", "obj");
        answers.Select(a => a.Verdict).Should().OnlyContain(v => v is GitVerdict.Retained && ((GitVerdict.Retained)v).Reason == "git refused: fatal: not a git repository: (NULL)");
    }

    [Fact]
    public async Task A_refused_ls_files_retains_the_whole_call_and_never_reaches_check_ignore_as_a_confirmation()
    {
        var repository = new FakeRepository { Ignored = { "bin" }, LsFiles = RecordingCommandRunner.Exited(128, stderr: "fatal: bad pathspec\n") };

        var answers = await Asker(repository.Runner()).AskAsync(Repo("bin"), CancellationToken.None);

        Of(answers, "bin").Should().Be(Retained("git refused: fatal: bad pathspec"));
    }

    [Theory]
    [InlineData(1, "")]
    [InlineData(0, "")]
    [InlineData(1, "\n")]
    public async Task An_empty_answer_with_a_nonzero_exit_retains(int exit, string stdout)
    {
        var repository = new FakeRepository { CheckIgnore = RecordingCommandRunner.Exited(exit, stdout) };

        var answers = await Asker(repository.Runner()).AskAsync(Repo("bin"), CancellationToken.None);

        Of(answers, "bin").Should().Be(Retained(GitReasons.NoAnswer), "no line is no answer, whatever the exit code says");
    }

    [Fact]
    public async Task An_exit_code_git_does_not_document_as_an_answer_retains_by_number_when_it_says_nothing()
    {
        var repository = new FakeRepository { CheckIgnore = RecordingCommandRunner.Exited(2) };

        var answers = await Asker(repository.Runner()).AskAsync(Repo("bin"), CancellationToken.None);

        Of(answers, "bin").Should().Be(Retained("git refused: exit code 2"));
    }

    [Fact]
    public async Task A_line_that_does_not_parse_answers_nothing()
    {
        var repository = new FakeRepository { CheckIgnore = RecordingCommandRunner.Exited(0, "garbage with no tab\nnot-a-rule\tobj\n.gitignore:1:bin/\tbin\n") };

        var answers = await Asker(repository.Runner()).AskAsync(Repo("bin", "obj"), CancellationToken.None);

        Of(answers, "bin").Should().Be(Confirmed);
        Of(answers, "obj").Should().Be(Retained(GitReasons.NoAnswer));
    }

    [Fact]
    public async Task A_timeout_a_failed_start_and_a_cut_output_each_retain_with_their_own_reason()
    {
        var timedOut = new FakeRepository { CheckIgnore = new CommandOutcome.TimedOut(CapturedText.Empty, CapturedText.Empty, TimeSpan.FromSeconds(30)) };
        var notStarted = new FakeRepository { RevParse = new CommandOutcome.FailedToStart("git.exe is not started: it is never looked up on PATH") };
        var cut = new FakeRepository { LsFiles = new CommandOutcome.Exited(0, new CapturedText("bin/a\0", Truncated: true), CapturedText.Empty, TimeSpan.Zero) };

        Of(await Asker(timedOut.Runner()).AskAsync(Repo("bin"), CancellationToken.None), "bin").Should().Be(Retained("git did not answer within 30 s"));
        Of(await Asker(notStarted.Runner()).AskAsync(Repo("bin"), CancellationToken.None), "bin").Should().Be(Retained("git could not be run: git.exe is not started: it is never looked up on PATH"));
        Of(await Asker(cut.Runner()).AskAsync(Repo("bin"), CancellationToken.None), "bin").Should().Be(Retained(GitReasons.Cut));
    }

    [Fact]
    public async Task A_name_that_would_spell_a_destructive_git_word_is_never_asked_and_is_retained_as_a_bad_name()
    {
        var runner = new FakeRepository().Runner();

        var answers = await Asker(runner).AskAsync(Repo("bin", "clean", "Prune", "worktree/"), CancellationToken.None);

        Of(answers, "bin").Should().Be(Retained(GitReasons.NotIgnored));
        Of(answers, "clean").Should().Be(Retained(GitReasons.BadName));
        Of(answers, "Prune").Should().Be(Retained(GitReasons.BadName));
        Of(answers, "worktree/").Should().Be(Retained(GitReasons.BadName));
        runner.Requests.SelectMany(r => r.Argv).Should().NotContain(["clean", "Prune", "worktree/"]);
    }

    [Fact]
    public async Task A_command_the_policy_refuses_retains_the_candidate_instead_of_running()
    {
        // The policy's own refusal reaches the answer as a retained candidate, never an exception: a planted runner that refuses everything.
        var runner = new RecordingCommandRunner { Policy = CommandPolicy.Over(CommandCatalogue.Empty) };

        var answers = await Asker(runner).AskAsync(Repo("bin"), CancellationToken.None);

        ((GitVerdict.Retained)Of(answers, "bin")).Reason.Should().StartWith("git could not be run: refused: no declared command template matches");
    }

    [Fact]
    public async Task A_stderr_complaint_is_one_line_of_printable_text_and_no_longer_than_the_reason_cap()
    {
        var nasty = "fatal: \u001b[31mbad\u0007 " + new string('x', 1000) + "\nsecond line\n";
        var repository = new FakeRepository { CheckIgnore = RecordingCommandRunner.Exited(128, stderr: nasty) };

        var answers = await Asker(repository.Runner()).AskAsync(Repo("bin"), CancellationToken.None);

        var reason = ((GitVerdict.Retained)Of(answers, "bin")).Reason;
        reason.Should().StartWith("git refused: fatal: ?[31mbad? ").And.NotContain("second line").And.NotContain("\n");
        reason.Length.Should().BeLessThanOrEqualTo("git refused: ".Length + GitReasons.MaxChars);
    }

    [Fact]
    public async Task Names_ride_calls_bounded_by_count_and_every_call_runs_both_questions()
    {
        var names = Enumerable.Range(0, 150).Select(i => $"p{i:000}/bin").ToArray();
        var repository = new FakeRepository { Ignored = [.. names] };
        var runner = repository.Runner();

        var answers = await Asker(runner).AskAsync(Repo(names), CancellationToken.None);

        answers.Should().OnlyContain(a => a.Verdict is GitVerdict.Confirmed);
        var checkIgnore = runner.Requests.Where(r => r.Argv.Contains("check-ignore")).Select(r => r.Argv.SkipWhile(a => a != "--").Skip(1).Count()).ToList();
        checkIgnore.Should().Equal(64, 64, 22);
        runner.Requests.Count(r => r.Argv.Contains("ls-files")).Should().Be(3);
        runner.Requests.Count(r => r.Argv.Contains("rev-parse")).Should().Be(1, "the top level is asked once per folder");
    }

    [Fact]
    public async Task Names_ride_calls_bounded_by_the_encoded_command_line_and_a_name_that_cannot_fit_alone_is_retained()
    {
        var longName = string.Join('/', Enumerable.Repeat(new string('d', 49), 5)) + "/bin"; // 253 characters: a legal name, too long for a 300-character line
        var names = new[] { "a/bin", longName, "b/bin", "c/bin" };
        var repository = new FakeRepository { Ignored = [.. names] };
        var runner = repository.Runner();
        var limits = new GitLimits(TimeSpan.FromSeconds(30), 64, 300);

        var answers = await Asker(runner, limits).AskAsync(Repo(names), CancellationToken.None);

        Of(answers, longName).Should().Be(Retained(GitReasons.TooLong), "a name that cannot fit one call alone is retained, never dropped");
        Of(answers, "a/bin").Should().Be(Confirmed);
        runner.Requests.Should().OnlyContain(r => WindowsCommandLine.Of("git.exe", r.Argv.Skip(1).ToList()).Length <= 300);
        runner.Requests.SelectMany(r => r.Argv).Should().NotContain(longName);
    }

    [Fact]
    public async Task The_environment_is_clean_plus_what_git_needs_and_has_no_home_and_the_ceiling_is_the_root()
    {
        var parent = new Dictionary<string, string>
        {
            ["SystemRoot"] = @"C:\Windows",
            ["WINDIR"] = @"C:\Windows",
            ["TEMP"] = @"C:\Temp",
            ["TMP"] = @"C:\Temp",
            ["HOME"] = @"C:\Users\me",
            ["USERPROFILE"] = @"C:\Users\me",
            ["XDG_CONFIG_HOME"] = @"C:\Users\me\.config",
            ["PATH"] = @"C:\evil",
            ["GIT_DIR"] = @"C:\evil\.git",
        };
        var runner = new FakeRepository { Ignored = { "bin" } }.Runner();

        await Asker(runner, parent: name => parent.GetValueOrDefault(name, string.Empty)).AskAsync(Repo("bin"), CancellationToken.None);

        runner.Requests.Should().HaveCount(3);
        foreach (var request in runner.Requests)
        {
            var environment = request.Environment.Should().BeOfType<CommandEnvironment.Clean>().Subject.Variables;
            environment.Should().BeEquivalentTo(new Dictionary<string, string>
            {
                ["SystemRoot"] = @"C:\Windows",
                ["WINDIR"] = @"C:\Windows",
                ["TEMP"] = @"C:\Temp",
                ["TMP"] = @"C:\Temp",
                ["GIT_OPTIONAL_LOCKS"] = "0",
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_CONFIG_GLOBAL"] = "NUL",
                ["GIT_CEILING_DIRECTORIES"] = Root,
            });
            request.WorkingDirectory.Should().Be(Folder, "the repository is the working directory, never a path on argv");
            request.Timeout.Should().Be(TimeSpan.FromSeconds(30));
            request.StdinClosed.Should().BeTrue();
            request.Utf8Output.Should().BeTrue("git prints paths as UTF-8");
            request.Argv.Take(8).Should().Equal(new[] { "git.exe" }.Concat(GitCommands.Options));
            request.Argv.Skip(1).Should().NotContain(a => a.Contains(Folder, StringComparison.OrdinalIgnoreCase), "no path travels on argv");
        }
    }

    [Fact]
    public async Task A_name_with_backslashes_is_asked_with_slashes_and_answered_under_its_own_spelling_and_a_bad_name_is_never_asked()
    {
        var repository = new FakeRepository { Ignored = { "src/bin" } };
        var runner = repository.Runner();

        var answers = await Asker(runner).AskAsync(Repo(@"src\bin", @"..\other", "a*b", string.Empty), CancellationToken.None);

        Of(answers, @"src\bin").Should().Be(Confirmed);
        Of(answers, @"..\other").Should().Be(Retained(GitReasons.BadName));
        Of(answers, "a*b").Should().Be(Retained(GitReasons.BadName));
        Of(answers, string.Empty).Should().Be(Retained(GitReasons.BadName));
        runner.Requests.SelectMany(r => r.Argv).Should().NotContain(new[] { @"..\other", "a*b", @"src\bin" });
        runner.Requests.SelectMany(r => r.Argv).Should().Contain("src/bin");
    }

    [Fact]
    public async Task A_non_ascii_name_is_asked_and_parsed_as_it_is_written()
    {
        var repository = new FakeRepository { Ignored = { "собранный/bin", "résumé/obj" } };

        var answers = await Asker(repository.Runner()).AskAsync(Repo("собранный/bin", "résumé/obj"), CancellationToken.None);

        answers.Should().OnlyContain(a => a.Verdict is GitVerdict.Confirmed);
    }

    [Fact]
    public async Task A_name_listed_twice_is_asked_once_and_answered_twice()
    {
        var runner = new FakeRepository { Ignored = { "bin" } }.Runner();

        var answers = await Asker(runner).AskAsync(Repo("bin", "BIN"), CancellationToken.None);

        answers.Should().HaveCount(2).And.OnlyContain(a => a.Verdict is GitVerdict.Confirmed);
        runner.Requests.Single(r => r.Argv.Contains("check-ignore")).Argv.SkipWhile(a => a != "--").Skip(1).Should().ContainSingle();
    }

    [Fact]
    public async Task No_names_ask_nothing()
    {
        var runner = new FakeRepository().Runner();

        (await Asker(runner).AskAsync(Repo(), CancellationToken.None)).Should().BeEmpty();
        runner.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_cancelled_question_throws_instead_of_answering()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = () => Asker(new FakeRepository().Runner()).AskAsync(Repo("bin"), cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private const string Admin = @"C:\repo\.git";

    private static GitQuestion Orphan(params string[] names) => new(Folder, names, Root, new GitFolder.Orphan(Admin));

    [Fact]
    public async Task An_orphan_with_its_repository_still_there_is_ignore_confirmed_and_its_tracked_files_are_not_checked()
    {
        var repository = new FakeRepository { Ignored = { "bin" } };
        var runner = repository.Runner();

        var answers = await Asker(runner).AskAsync(Orphan("bin", "obj"), CancellationToken.None);

        Of(answers, "bin").Should().Be(new GitVerdict.Confirmed(TrackedFilesChecked: false));
        ((GitVerdict.Confirmed)Of(answers, "bin")).Note.Should().Be("ignore rules confirmed; tracked files NOT checked");
        Of(answers, "obj").Should().Be(Retained(GitReasons.NotIgnored));
        runner.Requests.Should().ContainSingle("an orphan has no index to read and no top level to compare: one call");
        runner.Requests[0].Argv.Should().Contain(new[] { $"--git-dir={Admin}", "--work-tree=.", "--no-index" });
        runner.Requests[0].WorkingDirectory.Should().Be(Folder);
    }

    [Fact]
    public async Task An_orphan_whose_repository_is_gone_is_not_confirmed_and_nothing_is_started()
    {
        var runner = new FakeRepository { Ignored = { "bin" } }.Runner();

        var answers = await Asker(runner, exists: _ => false).AskAsync(Orphan("bin"), CancellationToken.None);

        Of(answers, "bin").Should().Be(Retained(GitReasons.OrphanNotConfirmed));
        runner.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_orphan_admin_folder_is_looked_up_as_given_to_the_existence_check()
    {
        var asked = new List<string>();
        var runner = new FakeRepository { Ignored = { "bin" } }.Runner();

        await Asker(runner, exists: path => { asked.Add(path); return true; }).AskAsync(Orphan("bin"), CancellationToken.None);

        asked.Should().Equal(Admin);
    }
}

/// <summary>The pure helpers under the questions: the batcher, the beneath-a-root check, and the two readers.</summary>
public sealed class GitSupportTests
{
    [Fact]
    public void Batches_hold_to_the_count_and_the_length_and_keep_the_order()
    {
        var names = Enumerable.Range(0, 10).Select(i => new string((char)('a' + i), 10)).ToList();

        var (calls, tooLong) = GitBatches.Split(names, maxCount: 4, maxChars: 1000, lengthOf: group => group.Sum(n => n.Length + 1));
        var (byLength, _) = GitBatches.Split(names, maxCount: 99, maxChars: 35, lengthOf: group => group.Sum(n => n.Length + 1));

        calls.Select(c => c.Count).Should().Equal(4, 4, 2);
        tooLong.Should().BeEmpty();
        byLength.Select(c => c.Count).Should().Equal(3, 3, 3, 1);
        byLength.SelectMany(c => c).Should().Equal(names);
    }

    [Fact]
    public void A_name_too_long_alone_is_set_aside_and_does_not_end_the_batch_before_it()
    {
        var (calls, tooLong) = GitBatches.Split(["a", new string('x', 50), "b"], maxCount: 8, maxChars: 20, lengthOf: group => group.Sum(n => n.Length + 1));

        tooLong.Should().Equal(new string('x', 50));
        calls.Should().ContainSingle().Which.Should().Equal("a", "b");
    }

    [Theory]
    [InlineData(@"D:\rsd\_wt", @"D:\rsd\_wt\app", true)]
    [InlineData(@"D:\rsd\_wt\", @"D:\rsd\_wt\app\", true)]
    [InlineData(@"d:/RSD/_wt", @"D:\rsd\_WT", true)]
    [InlineData(@"D:\rsd\_wt", @"D:\rsd\_wtx", false)]
    [InlineData(@"D:\rsd\_wt", @"D:\rsd", false)]
    [InlineData(@"D:\rsd\_wt", @"D:\rsd\_wt\..\x", false)]
    [InlineData(@"D:\rsd\_wt", @"D:\rsd\_wt\.\app", false)]
    [InlineData(@"D:\rsd\_wt", @"D:\rsd\_wt\\app", false)]
    [InlineData(@"D:\rsd\_wt", @"E:\rsd\_wt\app", false)]
    [InlineData(@"D:\rsd\_wt", @"app", false)]
    [InlineData(@"D:\rsd\_wt", @"\\server\share\_wt\app", false)]
    [InlineData(@"", @"D:\rsd", false)]
    [InlineData(@"D:\", @"D:\rsd", true)]
    public void A_folder_is_beneath_a_root_by_segments_and_never_by_prefix(string root, string folder, bool expected)
    {
        FolderBeneath.AtOrUnder(root, folder).Should().Be(expected);
    }

    [Fact]
    public void Check_ignore_lines_are_read_by_their_first_tab_and_their_first_digits_between_colons()
    {
        var lines = GitReplies.IgnoreLines(string.Join('\n',
            ".gitignore:1:bin/\tbin",
            "C:/Users/me/.gitignore:12:obj/:weird\tobj",
            "::\tbuild",
            ".gitignore:3:!keep\tkeep",
            "no tab here",
            ".gitignore::x\tbroken",
            ".gitignore:5:\tempty-pattern",
            ".gitignore:1:a\tb\tc") + "\n");

        lines["bin"].Should().Be(IgnoreLine.Ignored);
        lines["obj"].Should().Be(IgnoreLine.Ignored);
        lines["build"].Should().Be(IgnoreLine.NotIgnored);
        lines["keep"].Should().Be(IgnoreLine.NotIgnored);
        lines["broken"].Should().Be(IgnoreLine.Unreadable);
        lines["empty-pattern"].Should().Be(IgnoreLine.Unreadable);
        lines["b\tc"].Should().Be(IgnoreLine.Ignored, "the path is everything after the FIRST tab");
        lines.Should().HaveCount(7);
    }

    [Fact]
    public void A_path_named_twice_is_ignored_only_if_every_line_says_so_and_crlf_is_tolerated()
    {
        GitReplies.IgnoreLines(".gitignore:1:bin\tbin\r\n::\tbin\r\n")["bin"].Should().Be(IgnoreLine.NotIgnored);
        GitReplies.IgnoreLines("::\tbin\n.gitignore:1:bin\tbin\n")["bin"].Should().Be(IgnoreLine.NotIgnored);
        GitReplies.IgnoreLines(".gitignore:1:bin\tbin\r\n")["bin"].Should().Be(IgnoreLine.Ignored);
    }

    [Fact]
    public void Tracked_paths_are_split_on_nul_and_matched_by_folder_not_by_prefix()
    {
        var tracked = GitReplies.TrackedPaths("bin/a.dll\0bin\\b.dll\0binary/c\0");

        tracked.Should().Equal("bin/a.dll", "bin/b.dll", "binary/c");
        GitReplies.HoldsTracked(tracked, "bin").Should().BeTrue();
        GitReplies.HoldsTracked(tracked, "bin/").Should().BeTrue();
        GitReplies.HoldsTracked(tracked, "BIN").Should().BeTrue();
        GitReplies.HoldsTracked(tracked, "bi").Should().BeFalse();
        GitReplies.HoldsTracked(tracked, "obj").Should().BeFalse();
        GitReplies.HoldsTracked([], "bin").Should().BeFalse();
    }

    [Fact]
    public void The_environment_copies_only_four_names_from_its_parent()
    {
        var environment = GitEnvironment.For(name => name + "-value", @"D:\root");

        environment.Variables.Keys.Should().BeEquivalentTo(
            "SystemRoot", "WINDIR", "TEMP", "TMP", "GIT_OPTIONAL_LOCKS", "GIT_TERMINAL_PROMPT", "GIT_CONFIG_NOSYSTEM", "GIT_CONFIG_GLOBAL", "GIT_CEILING_DIRECTORIES");
        environment.Variables["GIT_CEILING_DIRECTORIES"].Should().Be(@"D:\root");
    }
}
