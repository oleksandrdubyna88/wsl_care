using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Agents;
using WslCare.Core.Archive;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Archive;

/// <summary>
/// Plan §15r D7 (E9.S0) — where the archive may live, judged in the distribution: a Windows drive path placed at its drvfs mount,
/// an existing folder never created, no link on the way, no root, not the home, nothing it moves or a cleanup removes, nothing a
/// shutdown empties, writable — and what a person should know when it is accepted. Over the distro's layout in a sandbox, so it
/// runs on every leg; the mode warnings are Linux's own.
/// </summary>
public sealed class BaseFolderRulesTests : IDisposable
{
    /// <summary>The mount table of the owner's distribution as observed (2026-10-04, WSL 2.7.10: the root disk and two drvfs
    /// drives, one of them a network drive), plus a tmpfs and a FAT stick to judge.</summary>
    private const string MountInfo =
        "523 504 8:96 / / rw,relatime - ext4 /dev/sdg rw,discard,errors=remount-ro,data=ordered\n" +
        "479 523 0:154 / /mnt/v rw,relatime - 9p V: rw,aname=drvfs;path=V:;uid=1000;gid=1000;metadata;symlinkroot=/mnt/,cache=0x5,access=client,msize=65536,trans=fd,rfd=3,wfd=3\n" +
        @"483 523 0:157 / /mnt/d rw,noatime - 9p D:\134 rw,aname=drvfs;path=D:\;uid=1000;gid=1000;symlinkroot=/mnt/,cache=0x5,access=client,msize=65536,trans=fd,rfd=6,wfd=6" + "\n" +
        "600 523 0:60 / /srv/ram rw,nosuid,nodev - tmpfs tmpfs rw,size=1024k\n" +
        "601 523 8:113 / /media/stick rw,relatime - vfat /dev/sdh1 rw,fmask=0022\n";

    private readonly LinuxSandbox _sandbox = new("base-folder");

    public BaseFolderRulesTests() => _sandbox.Write("/proc/self/mountinfo", MountInfo);

    public void Dispose() => _sandbox.Dispose();

    private string Folder(string distro)
    {
        var path = _sandbox.Paths.DistroPath(distro);
        Directory.CreateDirectory(path);
        return path;
    }

    private BaseFolderReport Judge(string given) =>
        BaseFolderRules.Judge(_sandbox.Paths, _sandbox.Files, ExtraAgentRules.CleanupRoots(ActionRegistry.Product, _sandbox.Paths.Home, _sandbox.Paths.Rules), given);

    [Fact]
    public void A_folder_on_the_network_drive_is_accepted_with_its_mount()
    {
        Folder("/mnt/v/ai-archive");

        var report = Judge("/mnt/v/ai-archive");

        report.Accepted.Should().BeTrue(report.Refusal);
        report.Folder.Should().Be("/mnt/v/ai-archive");
        report.Mount.Should().Be(new BaseMountReport("/mnt/v", "9p", "V:"));
        report.Notes.Should().ContainSingle(n => n.Contains("drvfs", StringComparison.Ordinal));
        report.Warnings.Should().BeEmpty("drvfs modes are the mount's report, not an answer about readers");
    }

    [Fact]
    public void A_windows_drive_path_is_answered_with_the_folder_it_is_mounted_at()
    {
        Folder("/mnt/v/ai-archive");

        var report = Judge(@"V:\ai-archive");

        report.Accepted.Should().BeTrue(report.Refusal);
        report.Folder.Should().Be("/mnt/v/ai-archive");
        report.Given.Should().Be(@"V:\ai-archive");
    }

    [Fact]
    public void A_drive_the_distribution_has_not_mounted_is_refused()
    {
        Judge(@"Q:\ai-archive").Should().Match<BaseFolderReport>(r => !r.Accepted && r.Rule == BaseFolderRule.DriveNotMounted);
    }

    [Fact]
    public void A_missing_base_folder_is_refused_and_never_created()
    {
        Folder("/mnt/v");

        var report = Judge("/mnt/v/ai-archive");

        report.Rule.Should().Be(BaseFolderRule.Missing);
        Directory.Exists(_sandbox.Paths.DistroPath("/mnt/v/ai-archive")).Should().BeFalse("the archive's folder is never created");
    }

    [Fact]
    public void A_file_is_not_a_base()
    {
        _sandbox.Write("/mnt/v/ai-archive", "x");

        Judge("/mnt/v/ai-archive").Rule.Should().Be(BaseFolderRule.NotAFolder);
    }

    [Theory]
    [InlineData("/mnt/v")]
    [InlineData(@"V:\")]
    [InlineData("/")]
    public void The_root_of_a_drive_or_a_filesystem_is_too_broad(string given)
    {
        Folder("/mnt/v");

        Judge(given).Rule.Should().Be(BaseFolderRule.TooBroad);
    }

    [Fact]
    public void The_home_itself_is_too_broad()
    {
        Folder("/home/me");

        Judge("/home/me").Rule.Should().Be(BaseFolderRule.TooBroad);
    }

    /// <summary>The archive never mixes with what it moves, nor with what a cleanup removes — an archive under <c>~/.cache</c> would
    /// be deleted by A17, one inside <c>~/.claude</c> would be inside what it archives.</summary>
    [Theory]
    [InlineData("/home/me/.claude/archive")]
    [InlineData("/home/me/.gemini")]
    [InlineData("/home")]
    [InlineData("/home/me/git/archive")]
    [InlineData("/tmp/claude/archive")]
    [InlineData("/tmp/archive")]
    [InlineData("/home/me/.cache/ms-playwright/archive")]
    [InlineData("/home/me/.config/wsl-care/archive")]
    [InlineData("/var/lib/wsl-care/archive")]
    public void A_base_inside_or_holding_an_agent_folder_git_claude_temp_a_cleanup_folder_or_the_products_own_is_refused(string given)
    {
        Folder(given);
        Folder("/home/me/.claude");

        Judge(given).Should().Match<BaseFolderReport>(r => !r.Accepted && r.Rule == BaseFolderRule.Overlap);
    }

    [Fact]
    public void A_filesystem_a_shutdown_empties_is_refused()
    {
        Folder("/srv/ram/archive");

        Judge("/srv/ram/archive").Should().Match<BaseFolderReport>(r => r.Rule == BaseFolderRule.Volatile && r.Refusal.Contains("tmpfs"));
    }

    [Fact]
    public void A_fat_drive_is_accepted_with_its_two_second_times_said()
    {
        Folder("/media/stick/archive");

        var report = Judge("/media/stick/archive");

        report.Accepted.Should().BeTrue(report.Refusal);
        report.Notes.Should().ContainSingle(n => n.Contains("2 seconds", StringComparison.Ordinal));
    }

    [Fact]
    public void A_base_on_the_distributions_own_disk_is_accepted_with_a_warning()
    {
        Folder("/srv/archive");

        var report = Judge("/srv/archive");

        report.Accepted.Should().BeTrue(report.Refusal);
        report.Warnings.Should().Contain(w => w.Contains("distribution's own disk", StringComparison.Ordinal));
    }

    [Fact]
    public void A_link_on_the_way_is_refused()
    {
        var real = Folder("/mnt/v/real");
        Folder("/mnt/v");
        Assert.SkipUnless(DirectoryLinks.TryCreate(_sandbox.Paths.DistroPath("/mnt/v/linked"), real), "this account may not create a directory link here");

        Judge("/mnt/v/linked").Rule.Should().Be(BaseFolderRule.LinkOnTheWay);
    }

    [Fact]
    public void Without_a_mount_table_the_base_is_refused()
    {
        File.Delete(_sandbox.Paths.DistroPath("/proc/self/mountinfo"));
        Folder("/mnt/v/ai-archive");

        Judge("/mnt/v/ai-archive").Rule.Should().Be(BaseFolderRule.MountUnreadable);
    }

    [Theory]
    [InlineData(@"\\nas\share\ai-archive", BaseFolderRule.Shape)]
    [InlineData("relative/folder", BaseFolderRule.Shape)]
    [InlineData("", BaseFolderRule.Shape)]
    public void A_share_or_a_relative_path_is_not_a_distribution_folder(string given, string rule)
    {
        Judge(given).Rule.Should().Be(rule);
    }

    /// <summary>Review M6: archived sessions hold what the agents saw — a folder others may read, or a parent others may write, is
    /// said; a private folder is not.</summary>
    [Fact]
    public void A_folder_other_accounts_may_read_is_accepted_with_a_warning_and_a_private_one_without()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("mode bits are the distribution's own (Windows asks the folder's access rules)");
            return;
        }

        var open = Folder("/srv/open");
        var closed = Folder("/srv/closed");
        File.SetUnixFileMode(open, (UnixFileMode)Convert.ToInt32("755", 8));
        File.SetUnixFileMode(closed, (UnixFileMode)Convert.ToInt32("700", 8));

        Judge("/srv/open").Warnings.Should().Contain(w => w.Contains("other accounts may read", StringComparison.Ordinal));
        Judge("/srv/closed").Warnings.Should().NotContain(w => w.Contains("other accounts may read", StringComparison.Ordinal));
    }

    [Fact]
    public void A_folder_this_account_may_not_write_is_refused()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            Assert.Skip("a mode bit stops a normal user on Linux; root writes anyway");
            return;
        }

        var folder = Folder("/srv/readonly");
        File.SetUnixFileMode(folder, (UnixFileMode)Convert.ToInt32("500", 8));

        try
        {
            Judge("/srv/readonly").Rule.Should().Be(BaseFolderRule.NotWritable);
        }
        finally
        {
            File.SetUnixFileMode(folder, (UnixFileMode)Convert.ToInt32("700", 8));
        }
    }
}
