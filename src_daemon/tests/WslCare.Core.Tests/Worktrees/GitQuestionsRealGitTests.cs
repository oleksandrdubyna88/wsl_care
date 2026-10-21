using System.Security.Cryptography;

using FluentAssertions;

using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Worktrees;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Worktrees;

/// <summary>
/// E19.S4 against the REAL <c>git.exe</c> (PLAN_disk_hygiene_windows_E19.md §3 decision 6): every test builds its own throw-away
/// <c>testsToDelete-…</c> repository, asks only read-only questions of it through the PRODUCT runner (the product policy, the real
/// <see cref="GitExecutable"/> with its real access-control checks), and removes it. Nothing here touches a real repository. The tests
/// whose claim is "X cannot leak in" each carry a CONTROL — the same situation without the protection, shown to leak — so a green run
/// proves the protection and not a quiet machine.
/// </summary>
public sealed class GitQuestionsRealGitTests
{
    private static readonly GitLimits Limits = new(TimeSpan.FromSeconds(60), 64, 7000);

    private static GitQuestions Product(Func<string, string>? parent = null) =>
        new(new ProcessCommandRunner(CommandPolicy.Product), Limits, parent ?? (name => GitEnvironment.ThisProcess(name)), Directory.Exists);

    private static GitQuestion Repository(ThrowAwayFolder repo, string root, params string[] names) => new(repo.Path, names, root, new GitFolder.Repository(repo.Path));

    private static GitVerdict Of(IReadOnlyList<GitAnswer> answers, string name) => answers.Single(a => a.Name == name).Verdict;

    private static void NeedGit() => Assert.SkipUnless(RealGit.Available, "this machine has no Git for Windows under Program Files (the real-git legs run on Windows)");

    [Fact]
    public async Task The_clean_environment_is_enough_for_real_git()
    {
        NeedGit();
        using var repo = new ThrowAwayGitRepo("git-clean");
        repo.Write(".gitignore", "/bin/\nobj/\npkg/bin/\n");
        repo.Write("bin/a.dll", "x");
        repo.Write("obj/b.dll", "x");
        repo.Write("docs/bin/x.txt", "x");
        repo.Write("pkg/bin/t.dll", "x");
        repo.Write("src/a.cs", "x");
        repo.Track("src/a.cs");
        repo.Track("pkg/bin/t.dll", force: true);

        var answers = await Product().AskAsync(Repository(repo, repo.Parent, "bin", "obj", "docs/bin", "pkg/bin"), CancellationToken.None);

        Of(answers, "bin").Should().Be(new GitVerdict.Confirmed(TrackedFilesChecked: true));
        Of(answers, "obj").Should().Be(new GitVerdict.Confirmed(TrackedFilesChecked: true));
        Of(answers, "docs/bin").Should().Be(new GitVerdict.Retained(GitReasons.NotIgnored), "no rule covers it: the root rule is anchored");
        Of(answers, "pkg/bin").Should().Be(new GitVerdict.Retained(GitReasons.Tracked), "ignored by a rule, but a file inside is tracked");
    }

    [Fact]
    public async Task A_user_excludes_file_does_not_change_the_answer()
    {
        NeedGit();
        using var home = new ThrowAwayFolder("git-home");
        var excludes = home.Write("excludes", "docs/bin/\n");
        home.Write(".gitconfig", $"[core]\n\texcludesFile = {excludes.Replace('\\', '/')}\n");
        home.Write(".config/git/ignore", "xdg/bin/\n");
        using var repo = new ThrowAwayGitRepo("git-leak");
        repo.Write(".gitignore", "/bin/\n");
        repo.Write("bin/a.dll", "x");
        repo.Write("docs/bin/x.txt", "x");
        repo.Write("xdg/bin/y.txt", "x");
        var leakyParent = new Dictionary<string, string>
        {
            ["HOME"] = home.Path,
            ["USERPROFILE"] = home.Path,
            ["XDG_CONFIG_HOME"] = home.Under(".config"),
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot") ?? string.Empty,
            ["WINDIR"] = Environment.GetEnvironmentVariable("WINDIR") ?? string.Empty,
            ["TEMP"] = Environment.GetEnvironmentVariable("TEMP") ?? string.Empty,
            ["TMP"] = Environment.GetEnvironmentVariable("TMP") ?? string.Empty,
        };

        // The control: with that home in its environment, git DOES answer with the user's rules as if they were the repository's.
        var leak = RealGit.Run(repo.Path, ["check-ignore", "-v", "-n", "--", "docs/bin", "xdg/bin"], new Dictionary<string, string> { ["HOME"] = home.Path, ["USERPROFILE"] = home.Path, ["XDG_CONFIG_HOME"] = home.Under(".config") });
        leak.Stdout.Should().Contain("excludes").And.Contain("docs/bin", "the control must show the leak, or this test proves nothing");

        var answers = await Product(name => leakyParent.GetValueOrDefault(name, string.Empty)).AskAsync(Repository(repo, repo.Parent, "bin", "docs/bin", "xdg/bin"), CancellationToken.None);

        Of(answers, "bin").Should().Be(new GitVerdict.Confirmed(TrackedFilesChecked: true));
        Of(answers, "docs/bin").Should().Be(new GitVerdict.Retained(GitReasons.NotIgnored), "the user's core.excludesFile is not the repository's rule");
        Of(answers, "xdg/bin").Should().Be(new GitVerdict.Retained(GitReasons.NotIgnored), "nor is the user's XDG ignore file");
    }

    [Fact]
    public async Task A_non_ascii_path_is_parsed()
    {
        NeedGit();
        using var repo = new ThrowAwayGitRepo("git-unicode");
        repo.Write(".gitignore", "bin/\nobj/\n");
        repo.Write("собранный/bin/a.dll", "x");
        repo.Write("résumé/obj/b.dll", "x");

        // The control: without core.quotePath=false git prints the path octal-escaped and quoted, which no name would match.
        var escaped = RealGit.Run(repo.Path, ["check-ignore", "-v", "-n", "--", "собранный/bin"]);
        escaped.Stdout.Should().Contain("\\3", "the control must show git escaping non-ASCII paths by default");

        var answers = await Product().AskAsync(Repository(repo, repo.Parent, "собранный/bin", "résumé/obj"), CancellationToken.None);

        answers.Should().OnlyContain(a => a.Verdict is GitVerdict.Confirmed, string.Join("; ", answers.Select(a => $"{a.Name}: {a.Verdict}")));
    }

    [Fact]
    public async Task The_scan_never_changes_the_index_time()
    {
        NeedGit();
        // M2 (2026-10-15): a `git status --ignored` sweep over 321 worktrees made 9 of them look active for days because git refreshed their
        // index. The idle signal the scan reads must survive the scan's own questions.
        using var repo = new ThrowAwayGitRepo("git-index");
        repo.Write(".gitignore", "bin/\n");
        repo.Write("bin/a.dll", "x");
        repo.Write("src/a.cs", "x");
        repo.Track("src/a.cs");
        var past = DateTime.UtcNow.AddDays(-3);
        File.SetLastWriteTimeUtc(repo.IndexFile, past);
        File.SetLastWriteTimeUtc(repo.Under("src/a.cs"), DateTime.UtcNow); // the stat data in the index is now stale: a refresh WOULD rewrite it
        var before = (File.GetLastWriteTimeUtc(repo.IndexFile), Hash(repo.IndexFile));

        var answers = await Product().AskAsync(Repository(repo, repo.Parent, "bin", "src"), CancellationToken.None);

        answers.Should().NotBeEmpty();
        (File.GetLastWriteTimeUtc(repo.IndexFile), Hash(repo.IndexFile)).Should().Be(before, "the three questions are read-only to the index");

        // The control: a plain `git status` refreshes the stale entry and rewrites the index, so the setup above really is one a careless
        // question would have disturbed.
        RealGit.Run(repo.Path, ["status", "--short"]);
        File.GetLastWriteTimeUtc(repo.IndexFile).Should().NotBe(before.Item1, "the control must show git touching the index, or this test proves nothing");
    }

    [Fact]
    public async Task Git_discovery_that_escapes_the_root_is_stopped_by_the_ceiling()
    {
        NeedGit();
        using var outer = new ThrowAwayGitRepo("git-outer");
        outer.Write("sub/inner/bin/a.dll", "x");
        outer.Write(".gitignore", "bin/\n");
        var inner = outer.Under("sub/inner");
        var root = outer.Under("sub");

        // The control: git started in the inner folder with no ceiling DOES find the outer repository.
        string.Equals(RealGit.Run(inner, ["rev-parse", "--show-toplevel"]).Stdout.Trim().Replace('/', Path.DirectorySeparatorChar), outer.Path, StringComparison.OrdinalIgnoreCase)
            .Should().BeTrue("the control must show discovery climbing out of the root");

        var answers = await Product().AskAsync(new GitQuestion(inner, ["bin"], root, new GitFolder.Repository(inner)), CancellationToken.None);

        Of(answers, "bin").Should().BeOfType<GitVerdict.Retained>().Which.Reason.Should().StartWith("git refused: fatal: not a git repository", "the ceiling stops discovery at the root");
    }

    [Fact]
    public async Task An_orphan_with_its_repository_still_there_is_ignore_confirmed()
    {
        NeedGit();
        using var repo = new ThrowAwayGitRepo("git-common");
        using var orphan = new ThrowAwayFolder("git-orphan");
        orphan.Write(".gitignore", "bin/\n");
        orphan.Write("bin/a.dll", "x");
        orphan.Write("obj/b.dll", "x");
        var admin = repo.Under(".git");

        var answers = await Product().AskAsync(new GitQuestion(orphan.Path, ["bin", "obj"], orphan.Parent, new GitFolder.Orphan(admin)), CancellationToken.None);

        Of(answers, "bin").Should().Be(new GitVerdict.Confirmed(TrackedFilesChecked: false), "the orphan's own .gitignore answers; its index is not read");
        Of(answers, "obj").Should().Be(new GitVerdict.Retained(GitReasons.NotIgnored));
    }

    [Fact]
    public async Task An_orphan_whose_repository_is_gone_is_not_confirmed_by_git()
    {
        NeedGit();
        using var orphan = new ThrowAwayFolder("git-orphan");
        orphan.Write(".gitignore", "bin/\n");
        orphan.Write("bin/a.dll", "x");

        var answers = await Product().AskAsync(new GitQuestion(orphan.Path, ["bin"], orphan.Parent, new GitFolder.Orphan(orphan.Under("gone/.git"))), CancellationToken.None);

        Of(answers, "bin").Should().Be(new GitVerdict.Retained(GitReasons.OrphanNotConfirmed));
    }

    [Fact]
    public async Task A_folder_that_is_not_a_repository_at_all_is_retained_with_gits_own_words()
    {
        NeedGit();
        using var folder = new ThrowAwayFolder("git-none");
        folder.Write("bin/a.dll", "x");

        var answers = await Product().AskAsync(new GitQuestion(folder.Path, ["bin"], folder.Path, new GitFolder.Repository(folder.Path)), CancellationToken.None);

        Of(answers, "bin").Should().BeOfType<GitVerdict.Retained>().Which.Reason.Should().StartWith("git refused: fatal: not a git repository");
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
