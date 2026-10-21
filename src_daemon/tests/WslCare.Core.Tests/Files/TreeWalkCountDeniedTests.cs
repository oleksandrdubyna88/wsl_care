using FluentAssertions;

using WslCare.Core.Files;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Files;

/// <summary>
/// E19.S3 (PLAN_disk_hygiene_windows.md §3.3 "The walk"): the walk that COUNTS what it could not open — a skipped, unreadable subtree
/// would hide a fresh write and let a worktree read idle — while every existing caller keeps ignoring it; and the three small things the
/// worktree scan asks of a walk: a folder it must not enter, names it must report, the newest write it saw.
/// </summary>
public sealed class TreeWalkCountDeniedTests : IDisposable
{
    private static readonly TreeLimits Limits = new(1_000_000, TimeSpan.FromMinutes(1));

    private static readonly IReadOnlySet<string> NoNames = new HashSet<string>(StringComparer.Ordinal);

    private readonly ToDeleteRoot _root = new("walk-denied");

    private readonly PhysicalFileSystem _files;

    public TreeWalkCountDeniedTests()
    {
        _files = new PhysicalFileSystem(Core.Hosting.HostPaths.ForThisMachine(_root.Path)) { OwnersAreThisProcess = true };
    }

    public void Dispose() => _root.Dispose();

    private TreeMeasure.Measured Walk(string path, TreeRules rules) =>
        _files.WalkTree(path, Limits, rules, CancellationToken.None).Should().BeOfType<TreeMeasure.Measured>().Subject;

    private string DeniedTree()
    {
        _root.File("tree/open/a.txt", new string('a', 10));
        _root.File("tree/closed/hidden.txt", new string('h', 20));
        return _root.Under("tree");
    }

    [Fact]
    public async Task A_denied_subfolder_is_counted_when_asked()
    {
        var tree = DeniedTree();
        await using var denial = await AccessDenial.TryDenyAsync(_root.Under("tree/closed"));
        Assert.SkipWhen(denial is null, "this account cannot be denied a folder here (root or elevated)");

        var measured = Walk(tree, TreeRules.All with { CountDenied = true });

        measured.Denied.Should().BeGreaterThan(0, "the closed folder could not be opened");
        measured.Files.Should().Be(1);
        measured.Bytes.Should().Be(10);
    }

    [Fact]
    public async Task Existing_walks_keep_ignoring_inaccessible_folders()
    {
        var tree = DeniedTree();
        await using var denial = await AccessDenial.TryDenyAsync(_root.Under("tree/closed"));
        Assert.SkipWhen(denial is null, "this account cannot be denied a folder here (root or elevated)");

        var rules = Walk(tree, TreeRules.All);
        var measure = _files.MeasureTree(tree, Limits, NoNames, NoNames, CancellationToken.None).Should().BeOfType<TreeMeasure.Measured>().Subject;

        rules.Denied.Should().Be(0, "a walk that was not asked to count says nothing of what it skipped");
        rules.Complete.Should().BeTrue();
        rules.Files.Should().Be(1);
        measure.Denied.Should().Be(0);
        measure.Complete.Should().BeTrue();
        measure.Files.Should().Be(1);
    }

    [Fact]
    public void A_walk_with_nothing_denied_counts_none()
    {
        var tree = DeniedTree();

        Walk(tree, TreeRules.All with { CountDenied = true }).Denied.Should().Be(0);
    }

    [Fact]
    public void A_skipped_folder_is_not_entered_and_its_own_write_is_still_seen()
    {
        _root.File("t/src/a.cs", "aaaa");
        _root.File("t/bin/x.dll", new string('x', 50));
        var when = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(_root.Under("t/bin"), when.AddDays(2));
        File.SetLastWriteTimeUtc(_root.Under("t/bin/x.dll"), when.AddDays(9));
        File.SetLastWriteTimeUtc(_root.Under("t/src/a.cs"), when);
        Directory.SetLastWriteTimeUtc(_root.Under("t/src"), when);
        Directory.SetLastWriteTimeUtc(_root.Under("t"), when);
        var skipped = new List<string>();
        var rules = TreeRules.All with
        {
            CountDenied = true,
            TrackNewest = true,
            SkipDirectory = (full, name) =>
            {
                if (name == "bin")
                {
                    skipped.Add(full);
                    return true;
                }

                return false;
            },
        };

        var measured = Walk(_root.Under("t"), rules);

        measured.Files.Should().Be(1);
        measured.Bytes.Should().Be(4);
        skipped.Should().Equal(_root.Under("t/bin"));
        measured.NewestWriteUtc.Should().Be(new DateTimeOffset(when.AddDays(2)), "the skipped folder's own write counts; what is inside it does not");
    }

    [Fact]
    public void The_newest_write_is_over_files_and_folders()
    {
        _root.File("t/d/f.txt", "x");
        var when = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_root.Under("t/d/f.txt"), when);
        Directory.SetLastWriteTimeUtc(_root.Under("t/d"), when.AddDays(5));
        Directory.SetLastWriteTimeUtc(_root.Under("t"), when.AddDays(30));

        var measured = Walk(_root.Under("t"), TreeRules.All with { TrackNewest = true });

        measured.NewestWriteUtc.Should().Be(new DateTimeOffset(when.AddDays(5)), "the root's own time is the caller's; entries below it are the walk's");
    }

    [Fact]
    public void An_untracked_walk_reports_no_newest()
    {
        _root.File("t/f.txt", "x");

        Walk(_root.Under("t"), TreeRules.All).NewestWriteUtc.Should().Be(DateTimeOffset.MinValue);
    }

    [Fact]
    public void Reported_names_are_found_whether_a_file_or_a_folder_and_even_when_never_entered()
    {
        _root.File("t/a/.git", "gitdir: x");
        _root.File("t/b/.GIT/HEAD", "ref");
        _root.File("t/c/readme.md", "x");
        var git = new HashSet<string>([".git"], StringComparer.OrdinalIgnoreCase);

        var measured = Walk(_root.Under("t"), TreeRules.All with { ReportNames = git, ReportNamesMax = 10, NeverEnter = git });

        measured.Found.Should().BeEquivalentTo([_root.Under("t/a/.git"), _root.Under("t/b/.GIT")]);
        measured.Files.Should().Be(2, "the folder named .git is not entered: its HEAD is not counted");
    }

    [Fact]
    public void Reported_names_are_bounded()
    {
        for (var i = 0; i < 5; i++)
        {
            _root.File($"t/d{i}/.git", "gitdir: x");
        }

        var measured = Walk(_root.Under("t"), TreeRules.All with { ReportNames = new HashSet<string>([".git"], StringComparer.OrdinalIgnoreCase), ReportNamesMax = 2 });

        measured.Found.Should().HaveCount(2);
    }

    [Fact]
    public void A_walk_that_reports_nothing_finds_nothing()
    {
        _root.File("t/a/.git", "gitdir: x");

        Walk(_root.Under("t"), TreeRules.All).Found.Should().BeEmpty();
    }

    [Fact]
    public void Never_entered_names_follow_the_comparer_of_their_set()
    {
        _root.File("t/x/.GIT/HEAD", "ref");
        _root.File("t/x/keep.txt", "k");

        var sensitive = Walk(_root.Under("t"), new TreeRules(NoNames, new HashSet<string>([".git"], StringComparer.Ordinal)));
        var insensitive = Walk(_root.Under("t"), new TreeRules(NoNames, new HashSet<string>([".git"], StringComparer.OrdinalIgnoreCase)));

        sensitive.Files.Should().Be(2);
        insensitive.Files.Should().Be(1);
    }

    /// <summary>E19.S3 Fable review, condition 2: the counting walk counted only the files it yielded, so a tree of many EMPTY folders ignored the entry
    /// budget, the deadline and the token. Every folder it recurses into counts, and the token is asked.</summary>
    [Fact]
    public void A_tree_of_many_empty_folders_still_stops_at_the_budget()
    {
        for (var i = 0; i < 300; i++)
        {
            Directory.CreateDirectory(_root.Under($"empty/d{i}"));
        }

        var stopped = _files.WalkTree(_root.Under("empty"), new TreeLimits(50, TimeSpan.FromMinutes(1)), TreeRules.All with { CountDenied = true }, CancellationToken.None)
            .Should().BeOfType<TreeMeasure.Measured>().Subject;

        stopped.Complete.Should().BeFalse("300 folders are past a budget of 50 entries, whether or not they hold a file");
        stopped.Note.Should().Contain("50 entries");
    }

    [Fact]
    public void A_tree_of_many_empty_folders_asks_the_token()
    {
        for (var i = 0; i < 1100; i++)
        {
            Directory.CreateDirectory(_root.Under($"empty-token/d{i}"));
        }

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var walk = () => _files.WalkTree(_root.Under("empty-token"), Limits, TreeRules.All with { CountDenied = true }, cancelled.Token);

        walk.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void A_tree_of_many_empty_folders_still_ends_a_walk_that_ignores_what_it_cannot_open_as_before()
    {
        for (var i = 0; i < 300; i++)
        {
            Directory.CreateDirectory(_root.Under($"plain/d{i}"));
        }

        var measured = _files.WalkTree(_root.Under("plain"), new TreeLimits(50, TimeSpan.FromMinutes(1)), TreeRules.All, CancellationToken.None)
            .Should().BeOfType<TreeMeasure.Measured>().Subject;

        measured.Complete.Should().BeTrue("existing callers keep today's behaviour: only files count toward their entry limit");
    }
}
