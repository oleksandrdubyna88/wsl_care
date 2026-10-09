using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Agents;
using WslCare.Core.Archive;
using WslCare.Core.Config;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Archive;

/// <summary>
/// The E9.S1 review round (plan §15r *E9.S1 review round*), the base rules' half, over the distro's layout in a sandbox: a
/// spelling with an empty or <c>.</c> segment never reaches the mount checks (B1); an 8.3 short name on drvfs is refused (M2);
/// a Windows-side manual agent's folders are protected from the distro (M3); without the Windows profile the profile-independent
/// places are still protected and the base is warned (m1); a drvfs mount of a loopback share is refused (m2); a second whole
/// mount of the root disk and a btrfs subvolume are judged as what they are (m6, m7).
/// </summary>
public sealed class BaseFolderS1ReviewTests : IDisposable
{
    // The source is escaped by the kernel, the super options are printed raw (main's PR #10 retro round, O3) — this table had
    // them escaped until the rebase of 2026-10-07, which the parser no longer decodes.
    private const string MountInfo =
        "523 504 8:96 / / rw,relatime - ext4 /dev/sdg rw,discard,errors=remount-ro,data=ordered\n" +
        @"481 523 0:153 / /mnt/c rw,noatime - 9p C:\134 rw,aname=drvfs;path=C:\;uid=1000;gid=1000;symlinkroot=/mnt/,cache=0x5,access=client,msize=65536,trans=fd,rfd=5,wfd=5" + "\n" +
        @"482 523 0:160 / /mnt/lh rw,noatime - 9p \134\134localhost\134C$ rw,aname=drvfs;path=\\localhost\C$;uid=1000;gid=1000;symlinkroot=/mnt/,cache=0x5,access=client,msize=65536,trans=fd,rfd=7,wfd=7" + "\n" +
        "600 523 0:60 / /dev/shm rw,nosuid,nodev - tmpfs tmpfs rw\n" +
        "702 523 8:96 / /data rw,relatime - ext4 /dev/sdg rw\n" +
        "703 523 8:96 /home/me/.claude /mnt/bound rw,relatime - ext4 /dev/sdg rw\n" +
        "706 523 0:46 /@srv /srv/sub rw,relatime - btrfs /dev/sdh rw,subvol=/@srv\n";

    private readonly LinuxSandbox _sandbox = new("base-s1-review");

    public BaseFolderS1ReviewTests() => _sandbox.Write("/proc/self/mountinfo", MountInfo);

    public void Dispose() => _sandbox.Dispose();

    private void Folder(string distro) => Directory.CreateDirectory(_sandbox.Paths.DistroPath(distro));

    private BaseFolderReport Judge(string given, string profile = "", params string[] windowsAgentFolders) =>
        BaseFolderRules.Judge(_sandbox.Paths, _sandbox.Files, new BaseFolderContext(ExtraAgentRules.CleanupRoots(ActionRegistry.Product, _sandbox.Paths.Home, _sandbox.Paths.Rules), profile) { WindowsAgentFolders = windowsAgentFolders }, given);

    /// <summary>B1: a spelling the kernel resolves to the same folder must not be judged as another one — an empty segment, a
    /// <c>.</c> segment, a doubled leading slash. Refused by the shape before any mount is looked at.</summary>
    [Theory]
    [InlineData("//mnt/c/Users/me/.claude")]
    [InlineData("/mnt/./c/Users/me/AppData/Local/Temp")]
    [InlineData("/mnt/c/Users/me/./.claude")]
    [InlineData("/mnt//c")]
    [InlineData("//mnt/bound/archive")]
    [InlineData("//dev/shm/x")]
    public void A_spelling_with_an_empty_or_dot_segment_is_refused_before_any_mount_is_looked_at(string given)
    {
        Folder(given.Replace("//", "/", StringComparison.Ordinal).Replace("/./", "/", StringComparison.Ordinal));

        Judge(given).Rule.Should().Be(BaseFolderRule.Shape, Judge(given).Refusal);
    }

    [Theory]
    [InlineData("/mnt/a//b", false)]
    [InlineData("/mnt/a/./b", false)]
    [InlineData(@"C:\a\.\b", false)]
    [InlineData(@"C:\a\\b", false)]
    [InlineData(@"\\nas\share\.\x", false)]
    [InlineData("/mnt/v/ai-archive/", true)]
    [InlineData(@"V:\ai-archive\", true)]
    [InlineData("/", true)]
    public void The_path_shape_refuses_an_empty_or_dot_segment_and_takes_one_trailing_separator(string value, bool accepted)
    {
        (ConfigValidation.Parse(ConfigKeys.Archive.BaseFolder, value) is ValueCheck.Ok).Should().Be(accepted);
        if (!value.StartsWith(@"\\", StringComparison.Ordinal))
        {
            (ExtraAgentShape.PathProblem(value, value.StartsWith('/') ? ExtraAgentShape.Wsl : ExtraAgentShape.Windows).Length == 0).Should().Be(accepted, "a manual agent's folder takes the same segment rule");
        }
    }

    /// <summary>M2: drvfs resolves an 8.3 short name to the long one; the distro's real path keeps the short spelling, so the
    /// Windows places would be compared by a spelling they do not have.</summary>
    [Theory]
    [InlineData("/mnt/c/Users/me/CLAUDE~1/archive")]
    [InlineData("/mnt/c/PROGRA~1/x")]
    public void A_short_name_segment_on_drvfs_is_refused(string given)
    {
        Folder(given);

        Judge(given, @"C:\Users\me").Rule.Should().Be(BaseFolderRule.Shape);
    }

    /// <summary>M3: a manual agent the WINDOWS side walks (aiAgents.extra, side "windows") keeps its folders protected from the
    /// distro too, case-blind.</summary>
    [Theory]
    [InlineData("/mnt/c/Users/me/tools/agentX/archive")]
    [InlineData("/mnt/c/users/ME/Tools/AgentX/archive")]
    [InlineData("/mnt/c/Users/me/tools")]
    public void A_base_inside_or_holding_a_windows_manual_agents_folder_is_refused(string given)
    {
        Folder(given);

        Judge(given, @"C:\Users\me", @"C:\Users\me\tools\agentX").Rule.Should().Be(BaseFolderRule.Overlap);
    }

    /// <summary>m1: without the Windows profile (no full run yet — normal at install) wsl-care's own Windows folder and any
    /// profile's repositories are still protected, and an accepted drvfs base says what was not judged.</summary>
    [Theory]
    [InlineData("/mnt/c/ProgramData/wsl-care/archive")]
    [InlineData("/mnt/c/Users/user/git/archive")]
    public void Without_the_profile_the_product_folder_and_any_profiles_repositories_are_refused(string given)
    {
        Folder(given);

        Judge(given).Rule.Should().Be(BaseFolderRule.Overlap);
    }

    [Fact]
    public void Without_the_profile_an_accepted_drvfs_base_is_warned()
    {
        Folder("/mnt/c/archive");

        var report = Judge("/mnt/c/archive");

        report.Accepted.Should().BeTrue(report.Refusal);
        report.Warnings.Should().Contain(w => w.Contains("Windows profile is unknown", StringComparison.Ordinal));
        Judge("/mnt/c/archive", @"C:\Users\me").Warnings.Should().NotContain(w => w.Contains("Windows profile is unknown", StringComparison.Ordinal));
    }

    /// <summary>m2: a drvfs mount of a share back to this machine reaches the drive under another name.</summary>
    [Fact]
    public void A_base_on_a_drvfs_mount_of_a_loopback_share_is_refused()
    {
        Folder("/mnt/lh/archive");

        Judge("/mnt/lh/archive").Rule.Should().Be(BaseFolderRule.Shape);
    }

    /// <summary>m6: the mount point of a second whole mount of the root disk IS the root — too broad, never an empty path.</summary>
    [Fact]
    public void The_mount_point_of_a_second_mount_of_the_root_disk_is_the_root()
    {
        Folder("/data");

        var report = Judge("/data");

        report.Rule.Should().Be(BaseFolderRule.TooBroad, report.Refusal);
        report.Folder.Should().Be("/");
    }

    /// <summary>m7: a btrfs subvolume mounted on its own is no bind mount of anything visible — judged as it is.</summary>
    [Fact]
    public void A_btrfs_subvolume_mounted_on_its_own_is_judged_as_it_is()
    {
        Folder("/srv/sub/archive");

        var report = Judge("/srv/sub/archive");

        report.Accepted.Should().BeTrue(report.Refusal);
        report.Folder.Should().Be("/srv/sub/archive");
    }
}
