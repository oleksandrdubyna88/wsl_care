using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Files;
using WslCare.Core.Folders;
using WslCare.Core.Health;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Folders;

/// <summary>The daily folder walk (plan §4.4) and the A8 / A9 figures: bounded, links never followed, contents never
/// read, once a day.</summary>
public sealed class FolderSizesTests : IDisposable
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>();
    private readonly TempRoot _root = new("folders");

    public void Dispose() => _root.Dispose();

    private PhysicalFileSystem Files => new(HostPaths.ForThisMachine(_root.Path));

    private static TreeMeasure.Measured Measured(TreeMeasure measure) => measure.Should().BeOfType<TreeMeasure.Measured>().Subject;

    [Fact]
    public void A_tree_is_the_sum_of_its_files()
    {
        _root.File("t/a.txt", new string('a', 100));
        _root.File("t/sub/b.txt", new string('b', 50));

        var m = Measured(Files.MeasureTree(_root.Under("t"), FolderSizes.Limits, None, None, CancellationToken.None));

        m.Should().Be(new TreeMeasure.Measured(150, 2, true, string.Empty));
    }

    [Fact]
    public void A_link_inside_the_tree_is_neither_counted_nor_entered()
    {
        _root.File("big/huge.bin", new string('x', 10_000));
        _root.File("t/a.txt", new string('a', 10));
        if (!DirectoryLinks.TryCreate(_root.Under("t/link"), _root.Under("big")))
        {
            Assert.Skip("this account can create neither a symlink nor a junction");
        }

        Measured(Files.MeasureTree(_root.Under("t"), FolderSizes.Limits, None, None, CancellationToken.None)).Bytes.Should().Be(10, "the 10 000 bytes behind the link are another tree's");
    }

    [Fact]
    public void A_folder_that_is_itself_a_link_is_not_walked()
    {
        _root.File("big/huge.bin", new string('x', 1000));
        if (!DirectoryLinks.TryCreate(_root.Under("npm"), _root.Under("big")))
        {
            Assert.Skip("this account can create neither a symlink nor a junction");
        }

        Files.MeasureTree(_root.Under("npm"), FolderSizes.Limits, None, None, CancellationToken.None).Should().BeOfType<TreeMeasure.Unreadable>().Which.Reason.Should().Contain("never follows");
    }

    [Fact]
    public void Build_output_counts_only_files_under_bin_and_obj_and_never_enters_node_modules()
    {
        _root.File("git/p/src/a.cs", new string('s', 1000));
        _root.File("git/p/bin/Release/p.dll", new string('b', 30));
        _root.File("git/p/obj/p.json", new string('o', 20));
        _root.File("git/web/node_modules/x/bin/cli.js", new string('n', 500));

        var m = Measured(Files.MeasureTree(_root.Under("git"), FolderSizes.Limits, new HashSet<string>(["bin", "obj"]), new HashSet<string>(["node_modules", ".git"]), CancellationToken.None));

        m.Bytes.Should().Be(50);
        m.Files.Should().Be(2);
    }

    [Fact]
    public void A_walk_that_reaches_its_entry_limit_stops_and_says_its_figure_is_a_lower_bound()
    {
        for (var i = 0; i < 5; i++)
        {
            _root.File($"t/f{i}.txt", "12345");
        }

        var m = Measured(Files.MeasureTree(_root.Under("t"), new TreeLimits(3, TimeSpan.FromMinutes(1)), None, None, CancellationToken.None));

        m.Complete.Should().BeFalse();
        m.Files.Should().Be(3);
        m.Note.Should().Contain("lower bound");
    }

    [Fact]
    public void A_missing_folder_is_missing_not_zero() =>
        Files.MeasureTree(_root.Under("nope"), FolderSizes.Limits, None, None, CancellationToken.None).Should().BeOfType<TreeMeasure.Missing>();

    [Fact]
    public void The_folders_are_walked_once_a_day()
    {
        var now = FixedTimeProvider.DefaultNow;
        var sample = new FolderSizesSample(now.AddHours(-19), []);
        var id = RunId.New(now.AddHours(-19), 1);

        FolderSizes.Due(Reading.Missing<AgedPart<FolderSizesSample>>("none yet"), now).Should().BeTrue();
        FolderSizes.Due(Reading.Of(new AgedPart<FolderSizesSample>(sample, id, sample.SampledAt, TimeSpan.FromHours(19))), now).Should().BeFalse();
        FolderSizes.Due(Reading.Of(new AgedPart<FolderSizesSample>(sample with { SampledAt = now.AddHours(-20) }, id, now.AddHours(-20), TimeSpan.FromHours(20))), now).Should().BeTrue();
    }

    [Fact]
    public async Task The_daily_sample_measures_the_npm_cache_the_apt_cache_and_the_disabled_snap_revisions()
    {
        var paths = new LinuxHostPaths(LinuxEnvironment.Sandboxed(_root.Path));
        _root.File("home/me/.npm/_cacache/x", new string('n', 300));
        _root.File("var/cache/apt/archives/a.deb", new string('a', 70));
        _root.File("var/lib/snapd/snaps/core22_2900.snap", new string('s', 40));
        _root.File("var/lib/snapd/snaps/core22_2955.snap", new string('s', 99));
        var snap = "Name    Version   Rev    Tracking       Publisher    Notes\ncore22  20260824  2955   latest/stable  canonical**  base\ncore22  20260701  2900   latest/stable  canonical**  base,disabled\n";
        var runner = new RecordingCommandRunner().Script(HealthCommands.SnapList.Argv, 0, snap);

        var sample = await new FolderSizes(new PhysicalFileSystem(paths), runner, new FixedTimeProvider()).MeasureAsync(paths, CancellationToken.None);

        sample.Find(FolderSizes.NpmCache)!.Should().Match<FolderSize>(f => f.Measured && f.Bytes == 300);
        sample.Find(FolderSizes.AptCache)!.Bytes.Should().Be(70);
        sample.Find(FolderSizes.SnapDisabled)!.Should().Match<FolderSize>(f => f.Bytes == 40 && f.Files == 1);
        sample.Find("git-worktrees")!.Unavailable.Should().Contain("does not exist");
        runner.Commands.Should().Equal(HealthCommands.SnapList.Display);
    }
}
