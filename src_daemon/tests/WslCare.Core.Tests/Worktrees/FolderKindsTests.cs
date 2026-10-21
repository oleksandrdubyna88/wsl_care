using System.Reflection;

using FluentAssertions;

using WslCare.Core.Files;
using WslCare.Core.Health;
using WslCare.Core.Collectors;
using WslCare.Core.Worktrees;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Worktrees;

/// <summary>
/// E19.S3 (PLAN_disk_hygiene_windows.md §3.3 "What each direct child folder of a root is"): the classifier answers from bounded,
/// no-link file reads alone — a worktree, an orphan, a foreign pointer, a main clone, not a git folder — with a locked flag and a
/// test-leftover flag, and it starts no process. Windows legs over a <c>testsToDelete-</c> tree.
/// </summary>
public sealed class FolderKindsTests
{
    private static FolderFacts Classify(WorktreeWorld world, string name, WorktreeSettings? settings = null)
    {
        var entry = world.Files.ListEntries(world.Work).Single(e => e.Name == name);
        return FolderKinds.Classify(world.Files, world.Paths, settings ?? WorktreeWorld.DefaultSettings(), world.Work, entry);
    }

    [Fact]
    public void A_git_file_pointing_back_is_a_worktree()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("kinds-worktree");
        world.Main("main");
        world.Worktree("wt1");

        var facts = Classify(world, "wt1");

        facts.Kind.Should().Be(FolderKind.Worktree, facts.Why);
        facts.AdminFolder.Should().Be(world.AdminOf("wt1"));
        facts.Locked.Should().BeFalse();
        facts.TestLeftover.Should().BeFalse();
        facts.Path.Should().Be(Path.Combine(world.Work, "wt1"));
    }

    [Fact]
    public void The_back_pointer_is_compared_without_case_and_with_either_slash()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("kinds-case");
        world.Main("main");
        world.Worktree("wt1", backPointer: Path.Combine(world.Work, "wt1", ".git").ToUpperInvariant().Replace('\\', '/'));

        Classify(world, "wt1").Kind.Should().Be(FolderKind.Worktree);
    }

    [Fact]
    public void A_git_file_pointing_nowhere_is_an_orphan()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("kinds-orphan");
        world.Main("main");
        world.Pointing("gone", $"gitdir: {WorktreeWorld.Forward(world.AdminOf("gone"))}\n");

        var facts = Classify(world, "gone");

        facts.Kind.Should().Be(FolderKind.Orphan);
        facts.Why.Should().Contain("gone");
    }

    [Fact]
    public void An_admin_folder_that_points_elsewhere_makes_an_orphan_of_the_folder_that_names_it()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("kinds-elsewhere");
        world.Main("main");
        world.Worktree("wt1", backPointer: WorktreeWorld.Forward(Path.Combine(world.Work, "somewhere-else", ".git")));

        Classify(world, "wt1").Kind.Should().Be(FolderKind.Orphan);
    }

    [Fact]
    public void An_admin_folder_with_no_back_pointer_is_an_orphan()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("kinds-noback");
        world.Main("main");
        world.Worktree("wt1");
        File.Delete(Path.Combine(world.AdminOf("wt1"), "gitdir"));

        Classify(world, "wt1").Kind.Should().Be(FolderKind.Orphan);
    }

    [Fact]
    public void A_relative_pointer_is_read_from_the_folder_it_lies_in()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("kinds-relative");
        world.Main("main");
        world.Worktree("wt1", pointerSpelling: "../main/.git/worktrees/wt1", backPointer: "../../../../wt1/.git");

        Classify(world, "wt1").Kind.Should().Be(FolderKind.Worktree);
    }

    /// <summary>E19.S3 Fable review, condition 3: a back pointer whose target EXISTS under another spelling (a link, an alias, a short name) is a
    /// live worktree the classifier cannot vouch for — never an orphan, never a candidate. Only a target that does not exist is an orphan.</summary>
    [Fact]
    public void An_admin_folder_pointing_at_this_folder_under_another_spelling_is_not_an_orphan()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("kinds-alias");
        world.Main("main");
        world.Worktree("wt1");
        var alias = world.Sandbox.Under("alias");
        Assert.SkipUnless(DirectoryLinks.TryCreate(alias, world.Work), "no junction could be made here");
        File.WriteAllText(Path.Combine(world.AdminOf("wt1"), "gitdir"), WorktreeWorld.Forward(Path.Combine(alias, "wt1", ".git")) + "\n");

        var facts = Classify(world, "wt1");

        facts.Kind.Should().NotBe(FolderKind.Orphan, facts.Why);
        facts.Kind.Should().Be(FolderKind.Unreadable);
        facts.Why.Should().Contain("another spelling");
    }

    [Fact]
    public void A_pointer_that_cannot_be_read_is_unreadable_and_never_an_orphan()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("kinds-unreadable");
        world.Main("main");
        world.Pointing("big", "gitdir: " + new string('x', 100_000));
        world.Pointing("garbage", "this is not a pointer\n");

        Classify(world, "big").Kind.Should().Be(FolderKind.Unreadable, "a file past the cap is not read, and not guessed at");
        var garbage = Classify(world, "garbage");
        garbage.Kind.Should().Be(FolderKind.Unreadable);
        garbage.Why.Should().Contain("gitdir");
    }

    [Fact]
    public void A_locked_file_marks_a_worktree_locked_and_shows_its_text_capped()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("kinds-locked");
        world.Main("main");
        world.Worktree("wt1");
        world.Worktree("wt2");
        world.Worktree("wt3");
        File.WriteAllText(Path.Combine(world.AdminOf("wt1"), "locked"), "on a usb stick\n");
        File.WriteAllText(Path.Combine(world.AdminOf("wt2"), "locked"), string.Empty);
        File.WriteAllText(Path.Combine(world.AdminOf("wt3"), "locked"), "long\u0007" + new string('r', 5000));
        var settings = WorktreeWorld.DefaultSettings();

        var one = Classify(world, "wt1");
        var two = Classify(world, "wt2");
        var three = Classify(world, "wt3");

        one.Locked.Should().BeTrue();
        one.LockedReason.Should().Be("on a usb stick");
        two.Locked.Should().BeTrue("git stores a lock as the file; an empty reason is still a lock");
        two.LockedReason.Should().BeEmpty();
        three.Locked.Should().BeTrue("a locked file past the cap still locks");
        three.LockedReason.Length.Should().BeLessThanOrEqualTo(settings.MaxReasonChars);
        three.LockedReason.Should().NotContain("\u0007");
        Classify(world, "main").Locked.Should().BeFalse();
    }

    [Fact]
    public void A_git_folder_is_a_main_clone_and_never_a_candidate()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("kinds-main");
        world.Main("main");

        var facts = Classify(world, "main");

        facts.Kind.Should().Be(FolderKind.MainClone);
        facts.AdminFolder.Should().BeEmpty();
    }

    [Fact]
    public void A_folder_with_no_git_is_not_a_git_folder()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("kinds-notgit");
        world.PlainFolder("docs");

        Classify(world, "docs").Kind.Should().Be(FolderKind.NotGit);
    }

    [Fact]
    public void A_wsl_made_worktree_with_a_mnt_pointer_is_a_worktree_not_an_orphan()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("kinds-wsl");
        world.Main("main");
        var posixAdmin = ((Reading<string>.Available)HealthCollector.InDistro(world.AdminOf("wsl1"), "/mnt/")).Value;
        var posixBack = ((Reading<string>.Available)HealthCollector.InDistro(Path.Combine(world.Work, "wsl1", ".git"), "/mnt/")).Value;
        world.Worktree("wsl1", pointerSpelling: posixAdmin, backPointer: posixBack);

        var facts = Classify(world, "wsl1");

        facts.Kind.Should().Be(FolderKind.Worktree, facts.Why);
        facts.AdminFolder.Should().Be(world.AdminOf("wsl1"), "the admin folder is reported as the Windows path it translates to");
    }

    [Fact]
    public void A_mnt_pointer_whose_folder_is_not_there_is_an_orphan()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("kinds-wsl-gone");
        world.Main("main");
        var posixAdmin = ((Reading<string>.Available)HealthCollector.InDistro(world.AdminOf("wsl1"), "/mnt/")).Value;
        world.Pointing("wsl1", $"gitdir: {posixAdmin}\n");

        Classify(world, "wsl1").Kind.Should().Be(FolderKind.Orphan);
    }

    [Theory]
    [InlineData("gitdir: /home/me/repo/.git/worktrees/x\n")]
    [InlineData("gitdir: /var/lib/x/.git/worktrees/x\n")]
    [InlineData("gitdir: //wsl.localhost/Ubuntu/home/me/repo/.git/worktrees/x\n")]
    [InlineData("gitdir: \\\\wsl.localhost\\Ubuntu\\home\\me\\repo\\.git\\worktrees\\x\n")]
    [InlineData("gitdir: \\\\server\\share\\repo\\.git\\worktrees\\x\n")]
    public void An_unmappable_posix_pointer_is_foreign_and_never_a_candidate(string pointer)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("kinds-foreign");
        world.Pointing("fx", pointer);

        var facts = Classify(world, "fx");

        facts.Kind.Should().Be(FolderKind.ForeignPointer, facts.Why);
    }

    [Fact]
    public void A_mnt_pointer_is_foreign_when_mnt_is_not_a_listed_automount_root()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("kinds-automount");
        world.Main("main");
        var posixAdmin = ((Reading<string>.Available)HealthCollector.InDistro(world.AdminOf("wsl1"), "/mnt/")).Value;
        var posixBack = ((Reading<string>.Available)HealthCollector.InDistro(Path.Combine(world.Work, "wsl1", ".git"), "/mnt/")).Value;
        world.Worktree("wsl1", pointerSpelling: posixAdmin, backPointer: posixBack);

        var facts = Classify(world, "wsl1", WorktreeWorld.DefaultSettings() with { AutomountRoots = ["/elsewhere/"] });

        facts.Kind.Should().Be(FolderKind.ForeignPointer, "the Windows side cannot read /etc/wsl.conf, so only the listed roots translate");
    }

    [Theory]
    [InlineData("testsToDelete-wt-one")]
    [InlineData("TESTSTODELETE-wt-two")]
    public void A_folder_named_with_the_test_leftover_prefix_is_flagged_whatever_it_is(string name)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("kinds-leftover");
        world.Main("main");
        world.Worktree(name);
        world.PlainFolder("not-testsToDelete-prefix");

        Classify(world, name).TestLeftover.Should().BeTrue();
        Classify(world, name).Kind.Should().Be(FolderKind.Worktree);
        Classify(world, "not-testsToDelete-prefix").TestLeftover.Should().BeFalse("the prefix is anchored at the start of the name");
    }

    [Fact]
    public void A_folder_that_is_a_link_is_a_link_and_nothing_inside_it_is_read()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WorktreeWorld.WindowsOnly);
        using var world = new WorktreeWorld("kinds-link");
        world.Main("main");
        var real = world.Worktree("real-wt");
        Assert.SkipUnless(DirectoryLinks.TryCreate(Path.Combine(world.Work, "lnk"), real), "no junction could be made here");

        var facts = Classify(world, "lnk");

        facts.Kind.Should().Be(FolderKind.Link);
        facts.AdminFolder.Should().BeEmpty();
    }

    [Fact]
    public void The_classifier_and_the_scan_start_no_process()
    {
        var offenders = Directory.EnumerateFiles(WorktreeSources.Directory, "*.cs")
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(f), @"\bICommandRunner\b|\bCommandRequest\b|\bCommandTemplate\b|\bProcessStartInfo\b|\bProcess\s*\.\s*Start\b|ProcessCommandRunner"))
            .Select(Path.GetFileName)
            .ToList();

        offenders.Should().BeEmpty("the worktree classifier and scan reach the disk through IFileSystem and nothing else — git's three questions are E19.S4's, and a different file");
        Directory.EnumerateFiles(WorktreeSources.Directory, "*.cs").Should().Contain(f => f.EndsWith("FolderKinds.cs", StringComparison.Ordinal), "the scan reads the real source folder");
    }
}

/// <summary>Where the product's <c>Worktrees</c> source lives, stamped by the build.</summary>
internal static class WorktreeSources
{
    public static string Directory
    {
        get
        {
            var root = typeof(WorktreeSources).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "WslCare.SourceRoot").Value!;
            return Path.Combine(root, "WslCare.Core", "Worktrees");
        }
    }
}
