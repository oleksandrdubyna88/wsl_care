using FluentAssertions;

using WslCare.Core.Files;
using WslCare.Core.Collectors;
using WslCare.Core.Hosting;
using WslCare.Core.Worktrees;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Worktrees;

/// <summary>
/// E19.S3 (PLAN_disk_hygiene_windows.md §3.3, E19 stories §3 decision 3): the rules a worktree root must pass — absolute, on a local
/// FIXED drive, a folder that exists, not a drive root, no link on the way, not the profile or an ancestor of it, not in or around an
/// agent's folder or Claude's temporary folder, not nested in or over another root, at most <c>worktrees.maxRoots</c>. Judged over a
/// Windows layout on every operating system (the disk is a table), plus the Windows legs over the real disk.
/// </summary>
public sealed class WorktreeRootsTests
{
    private const string Sandbox = @"D:\sandbox";

    private static readonly WindowsHostPaths Paths = new(WindowsEnvironment.Sandboxed(Sandbox));

    private static readonly PathRules Rules = PathRules.Windows;

    private static IReadOnlyList<RootVerdict> Judge(RootsDisk disk, int maxRoots, params string[] given) =>
        WorktreeRoots.Judge(Paths, disk, letter => disk.DriveOf(letter), maxRoots, given);

    private static RootVerdict JudgeOne(RootsDisk disk, string given) => Judge(disk, 8, given).Single();

    private static RootsDisk Disk(params string[] folders) => new(Paths, folders);

    [Theory]
    [InlineData("")]
    [InlineData("work")]
    [InlineData(@"\work")]
    [InlineData("D:work")]
    [InlineData("/mnt/d/work")]
    [InlineData(@"\\server\share\work")]
    [InlineData(@"\\?\D:\work")]
    [InlineData(@"\\.\D:\work")]
    [InlineData(@"D:\work\..\other")]
    [InlineData(@"D:\work\.\x")]
    [InlineData("D:\\work\u0007")]
    public void A_relative_or_network_or_device_root_is_refused_by_its_shape(string given)
    {
        var verdict = JudgeOne(Disk(@"D:\work"), given);

        verdict.Accepted.Should().BeFalse();
        verdict.Rule.Should().Be(RootRule.Shape, verdict.Reason);
        verdict.Reason.Should().NotBeEmpty();
    }

    [Fact]
    public void A_drive_root_is_refused()
    {
        var verdict = JudgeOne(Disk(@"D:\"), @"D:\");

        verdict.Accepted.Should().BeFalse();
        verdict.Rule.Should().Be(RootRule.DriveRoot, verdict.Reason);
    }

    [Theory]
    [InlineData(DriveType.Network)]
    [InlineData(DriveType.Removable)]
    [InlineData(DriveType.CDRom)]
    [InlineData(DriveType.Ram)]
    [InlineData(DriveType.NoRootDirectory)]
    [InlineData(DriveType.Unknown)]
    public void A_folder_on_a_drive_that_is_not_a_local_fixed_one_is_refused(DriveType kind)
    {
        var disk = Disk(@"D:\work");
        disk.Drives['D'] = kind;

        var verdict = JudgeOne(disk, @"D:\work");

        verdict.Accepted.Should().BeFalse();
        verdict.Rule.Should().Be(RootRule.NotLocalFixed, verdict.Reason);
        verdict.Reason.Should().Contain("D:");
    }

    [Fact]
    public void A_folder_that_is_not_there_or_is_a_file_is_refused()
    {
        var disk = Disk(@"D:\work");
        disk.Files.Add(@"D:\work\a-file");

        JudgeOne(disk, @"D:\nothing").Rule.Should().Be(RootRule.Missing);
        JudgeOne(disk, @"D:\work\a-file").Rule.Should().Be(RootRule.NotAFolder);
    }

    [Fact]
    public void A_folder_reached_through_a_link_is_refused()
    {
        var disk = Disk(@"D:\work", @"D:\real", @"D:\work\linked");
        disk.Links[@"D:\work\linked"] = @"D:\real";

        var verdict = JudgeOne(disk, @"D:\work\linked");

        verdict.Accepted.Should().BeFalse();
        verdict.Rule.Should().Be(RootRule.LinkOnTheWay, verdict.Reason);
    }

    [Fact]
    public void The_profile_and_every_ancestor_of_it_are_refused_but_a_folder_inside_it_is_not()
    {
        var home = Paths.Home;
        var disk = Disk(home, Rules.Parent(home), Sandbox, Rules.Join(home, "source"));

        JudgeOne(disk, home).Rule.Should().Be(RootRule.Profile);
        JudgeOne(disk, Rules.Parent(home)).Rule.Should().Be(RootRule.Profile, "an ancestor of the profile holds it");
        JudgeOne(disk, Sandbox).Rule.Should().Be(RootRule.Profile);
        JudgeOne(disk, Rules.Join(home, "source")).Accepted.Should().BeTrue("a folder inside the profile is the ordinary place for repositories");
    }

    [Fact]
    public void A_root_holding_an_agent_folder_or_inside_one_or_around_claudes_temp_is_refused()
    {
        var agent = Paths.AgentRoots.First();
        var claudeTemp = Paths.ClaudeTempRoots.Single();
        var insideAgent = Rules.Join(agent, "sessions");
        var aroundClaudeTemp = Rules.Parent(claudeTemp);
        var insideClaudeTemp = Rules.Join(claudeTemp, "work");
        var disk = Disk(agent, insideAgent, claudeTemp, aroundClaudeTemp, insideClaudeTemp);

        JudgeOne(disk, agent).Rule.Should().Be(RootRule.Protected);
        JudgeOne(disk, insideAgent).Rule.Should().Be(RootRule.Protected, "inside an agent's folder");
        JudgeOne(disk, claudeTemp).Rule.Should().Be(RootRule.Protected);
        JudgeOne(disk, insideClaudeTemp).Rule.Should().Be(RootRule.Protected, "inside Claude's temp folder, which is never cleaned");
        JudgeOne(disk, aroundClaudeTemp).Rule.Should().Be(RootRule.Protected, "a root that HOLDS Claude's temp folder would take it along");
    }

    /// <summary>E19.S3 Fable review, recommended 5: the system folders and the whole temporary folder are never a root, nor inside or around one.</summary>
    [Theory]
    [InlineData("Windows")]
    [InlineData("Program Files")]
    [InlineData("Program Files (x86)")]
    [InlineData("ProgramData")]
    [InlineData(@"Users\me\AppData\Local\Temp")]
    public void A_system_folder_or_the_temporary_folder_is_refused_and_so_is_one_inside_it(string relative)
    {
        var place = Rules.Join(Sandbox, relative);
        var inside = Rules.Join(place, "repos");
        var disk = Disk(place, inside);

        JudgeOne(disk, place).Rule.Should().Be(RootRule.Protected);
        JudgeOne(disk, inside).Rule.Should().Be(RootRule.Protected, "inside " + relative);
    }

    [Fact]
    public void A_folder_holding_the_temporary_folder_or_a_system_folder_is_refused()
    {
        var local = Rules.Join(Sandbox, @"Users\me\AppData\Local");
        var disk = Disk(local, Rules.Join(local, "Temp"));

        JudgeOne(disk, local).Accepted.Should().BeFalse("it holds the temporary folder");
    }

    [Fact]
    public void A_nested_or_overlapping_root_is_refused_and_the_earlier_one_kept()
    {
        var disk = Disk(@"D:\w", @"D:\w\sub", @"E:\w");

        var inner = Judge(disk, 8, @"D:\w", @"D:\w\sub");
        var outer = Judge(disk, 8, @"D:\w\sub", @"D:\w");
        var same = Judge(disk, 8, @"D:\w", "d:/W/");
        var apart = Judge(disk, 8, @"D:\w", @"E:\w");

        inner.Select(v => v.Accepted).Should().Equal(true, false);
        inner[1].Rule.Should().Be(RootRule.Overlap);
        inner[1].Reason.Should().Contain(@"D:\w");
        outer.Select(v => v.Accepted).Should().Equal(true, false);
        outer[1].Rule.Should().Be(RootRule.Overlap, "the earlier of two overlapping roots is the one kept");
        same.Select(v => v.Accepted).Should().Equal(true, false);
        apart.Select(v => v.Accepted).Should().Equal(true, true);
    }

    [Fact]
    public void A_list_past_the_maximum_is_refused_from_the_first_root_over_it()
    {
        var disk = Disk(@"D:\a", @"D:\b", @"D:\c");

        var verdicts = Judge(disk, 2, @"D:\a", @"D:\b", @"D:\c");

        verdicts.Select(v => v.Accepted).Should().Equal(true, true, false);
        verdicts[2].Rule.Should().Be(RootRule.TooMany);
        verdicts[2].Reason.Should().Contain("2");
    }

    [Fact]
    public void Every_path_gets_its_own_verdict_in_order_with_the_spelling_the_key_stores()
    {
        var disk = Disk(@"D:\work", @"E:\other");

        var verdicts = Judge(disk, 8, "d:/work/", "relative", @"e:\other");

        verdicts.Select(v => v.Given).Should().Equal("d:/work/", "relative", @"e:\other");
        verdicts.Select(v => v.Accepted).Should().Equal(true, false, true);
        verdicts.Select(v => v.Path).Should().Equal(@"D:\work", string.Empty, @"E:\other");
        verdicts[1].Reason.Should().NotBeEmpty();
    }

    [Fact]
    public void A_real_folder_on_a_fixed_drive_is_accepted_and_the_sandbox_that_holds_the_profile_is_not()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("roots-real");

        var verdicts = WorktreeRoots.Judge(world.Paths, world.Files, WorktreeRoots.ThisMachinesDrive, 8, [world.Work, world.Sandbox.Path]);

        verdicts[0].Accepted.Should().BeTrue(verdicts[0].Reason);
        verdicts[1].Accepted.Should().BeFalse("the sandbox holds the sandboxed profile");
        verdicts[1].Rule.Should().Be(RootRule.Profile);
    }

    [Fact]
    public void A_real_junction_is_refused_as_a_link_on_the_way()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("roots-junction");
        var real = world.Sandbox.Dir("real");
        Assert.SkipUnless(DirectoryLinks.TryCreate(world.Sandbox.Under("linked"), real), "no junction could be made here");

        var verdict = WorktreeRoots.Judge(world.Paths, world.Files, WorktreeRoots.ThisMachinesDrive, 8, [world.Sandbox.Under("linked")]).Single();

        verdict.Accepted.Should().BeFalse();
        verdict.Rule.Should().Be(RootRule.LinkOnTheWay, verdict.Reason);
    }

    /// <summary>A table standing in for the disk: the folders and files that exist, the links among them and the drives' kinds.</summary>
    private sealed class RootsDisk : DelegatingFileSystem
    {
        public RootsDisk(WindowsHostPaths paths, IEnumerable<string> folders)
            : base(new PhysicalFileSystem(paths))
        {
            Folders = [.. folders.Select(Rules.Normalize)];
        }

        public HashSet<string> Folders { get; }

        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> Links { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<char, DriveType> Drives { get; } = new();

        public DriveType DriveOf(char letter) => Drives.GetValueOrDefault(char.ToUpperInvariant(letter), DriveType.Fixed);

        public override bool DirectoryExists(string path) => Folders.Contains(Rules.Normalize(path), StringComparer.OrdinalIgnoreCase);

        public override bool FileExists(string path) => Files.Contains(Rules.Normalize(path));

        /// <summary>The table spells every folder as it is written: no 8.3 names here (the real-disk legs cover those).</summary>
        public override Reading<string> FolderFinalPath(string path) => Reading.Of(Rules.Normalize(path));

        public override RealPathResult ResolvePath(string path) =>
            new RealPathResult.Resolved(Links.TryGetValue(Rules.Normalize(path), out var target) ? target : Rules.Normalize(path));
    }
}
