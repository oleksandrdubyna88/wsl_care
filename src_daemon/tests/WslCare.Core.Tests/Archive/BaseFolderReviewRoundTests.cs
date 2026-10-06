using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Agents;
using WslCare.Core.Archive;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Archive;

/// <summary>
/// The E9.S0 review round (plan §15r *E9.S0 review round*) over the distro's layout in a sandbox, every leg: a base on a Windows
/// drive is judged against the Windows profile's places too, case-blind (S1); the distribution's own disk is a DEVICE, and a bind
/// mount is judged where it really lies (S4); a folder on the way owned by another account is said (S5); and a refusal answers an
/// empty mount, never null.
/// </summary>
public sealed class BaseFolderReviewRoundTests : IDisposable
{
    /// <summary>The owner's root disk and C: as WSL 2 mounts them, a second mount of the root disk, a bind mount of a folder inside
    /// the home, and a bind mount whose device's own root is mounted nowhere.</summary>
    private const string MountInfo =
        "523 504 8:96 / / rw,relatime - ext4 /dev/sdg rw,discard,errors=remount-ro,data=ordered\n" +
        @"481 523 0:153 / /mnt/c rw,noatime - 9p C:\134 rw,aname=drvfs;path=C:\;uid=1000;gid=1000;symlinkroot=/mnt/,cache=0x5,access=client,msize=65536,trans=fd,rfd=5,wfd=5" + "\n" +
        "702 523 8:96 / /data rw,relatime - ext4 /dev/sdg rw\n" +
        "703 523 8:96 /home/me/.claude /mnt/bound rw,relatime - ext4 /dev/sdg rw\n" +
        "704 523 8:200 /srv/data /mnt/orphan rw,relatime - ext4 /dev/sdz rw\n" +
        "705 523 0:45 / /srv/sub rw,relatime - btrfs /dev/sdg rw,subvol=/@sub\n";

    private const string Profile = @"C:\Users\me";

    private readonly LinuxSandbox _sandbox = new("base-review");

    public BaseFolderReviewRoundTests() => _sandbox.Write("/proc/self/mountinfo", MountInfo);

    public void Dispose() => _sandbox.Dispose();

    private void Folder(string distro) => Directory.CreateDirectory(_sandbox.Paths.DistroPath(distro));

    private BaseFolderReport Judge(string given, string profile = "") =>
        BaseFolderRules.Judge(_sandbox.Paths, _sandbox.Files, new BaseFolderContext(ExtraAgentRules.CleanupRoots(ActionRegistry.Product, _sandbox.Paths.Home, _sandbox.Paths.Rules), profile), given);

    /// <summary>S1: a base on C: inside the Windows profile's agent folders, its temporary folder, Claude's temporary folder, its
    /// repositories or the profile itself — spelt in any case, as NTFS compares — is refused once the profile is known.</summary>
    [Theory]
    [InlineData("/mnt/c/Users/me/.claude/archive")]
    [InlineData("/mnt/c/Users/ME/.Claude/archive")]
    [InlineData("/mnt/c/Users/me/AppData/Local/Temp/archive")]
    [InlineData("/mnt/c/Users/me/AppData/Local/Temp/claude/archive")]
    [InlineData("/mnt/c/Users/me/AppData/Roaming/Antigravity/archive")]
    [InlineData("/mnt/c/Users/me/git/archive")]
    [InlineData("/mnt/c/Users/ME/Git/archive")]
    [InlineData("/mnt/c/Users/me")]
    public void A_drvfs_base_inside_or_holding_a_windows_profiles_place_is_refused(string given)
    {
        Folder(given);

        Judge(given, Profile).Should().Match<BaseFolderReport>(r => r.Rule == BaseFolderRule.Overlap, Judge(given, Profile).Refusal);
    }

    /// <summary>S1, the profile unknown (no full run has found it): any profile's AppData and agent folders on any drive, a profile
    /// and the folder of all profiles are refused — whoever's they are.</summary>
    [Theory]
    [InlineData("/mnt/c/Users/user/AppData/Local/archive")]
    [InlineData("/mnt/c/Users/user/.codex/archive")]
    [InlineData("/mnt/c/Users/user")]
    [InlineData("/mnt/c/Users")]
    public void Without_the_profile_any_profiles_appdata_or_agent_folder_is_refused(string given)
    {
        Folder(given);

        Judge(given).Rule.Should().Be(BaseFolderRule.Overlap);
    }

    [Theory]
    [InlineData("")]
    [InlineData(Profile)]
    public void A_folder_of_its_own_in_the_profile_is_accepted(string profile)
    {
        Folder("/mnt/c/Users/me/Documents/ai-archive");

        var report = Judge("/mnt/c/Users/me/Documents/ai-archive", profile);

        report.Accepted.Should().BeTrue(report.Refusal);
    }

    /// <summary>S4: the distribution's own disk is its DEVICE — a second mount of it (<c>/data</c>) is the same disk, and the same
    /// folders: <c>/data/archive</c> IS <c>/archive</c>, and <c>/data/home/me/.claude</c> is the agent's folder.</summary>
    [Fact]
    public void A_base_on_a_second_mount_of_the_root_disk_is_judged_as_the_folder_it_is_on_the_distributions_own_disk()
    {
        Folder("/data/archive");
        Folder("/archive");
        Folder("/data/home/me/.claude/archive");
        Folder("/home/me/.claude/archive");

        var report = Judge("/data/archive");

        report.Folder.Should().Be("/archive", report.Refusal);
        report.Warnings.Should().Contain(w => w.Contains("distribution's own disk", StringComparison.Ordinal));
        Judge("/data/home/me/.claude/archive").Rule.Should().Be(BaseFolderRule.Overlap);
    }

    /// <summary>S4: a subvolume of the root disk has a device number of its own but the same source — the same disk.</summary>
    [Fact]
    public void A_base_on_a_subvolume_of_the_root_disk_is_the_distributions_own_disk()
    {
        Folder("/srv/sub/archive");

        var report = Judge("/srv/sub/archive");

        report.Accepted.Should().BeTrue(report.Refusal);
        report.Warnings.Should().Contain(w => w.Contains("distribution's own disk", StringComparison.Ordinal));
    }

    /// <summary>S4: a bind mount of <c>~/.claude</c> at <c>/mnt/bound</c> — the folder IS inside the agent's, whatever its spelling.</summary>
    [Fact]
    public void A_base_through_a_bind_mount_is_judged_where_it_really_lies()
    {
        Folder("/mnt/bound/archive");
        Folder("/home/me/.claude/archive");

        var report = Judge("/mnt/bound/archive");

        report.Rule.Should().Be(BaseFolderRule.Overlap, report.Refusal);
        report.Refusal.Should().Contain("/home/me/.claude");
    }

    /// <summary>E9.S1 review round m7 (it was refused here as "mounted whole nowhere", a false reason): a mount whose filesystem no
    /// other visible mount shows aliases nothing visible — every protected place lies on a mount the table shows, and a protected
    /// place on that filesystem would show as another mount of it. Judged as it is.</summary>
    [Fact]
    public void A_bind_mount_whose_filesystem_no_other_mount_shows_is_judged_as_it_is()
    {
        Folder("/mnt/orphan/archive");

        var report = Judge("/mnt/orphan/archive");

        report.Accepted.Should().BeTrue(report.Refusal);
        report.Folder.Should().Be("/mnt/orphan/archive");
    }

    /// <summary>S5: a folder on the way owned by another account (not root, not this one) could be renamed away under the archive.</summary>
    [Fact]
    public void A_folder_on_the_way_owned_by_another_account_is_said()
    {
        var warnings = BaseFolderRules.OwnerWarnings([("/mnt/x/archive", 1000u), ("/mnt/x", 1001u), ("/mnt", 0u)], 1000);

        warnings.Should().ContainSingle().Which.Should().Contain("/mnt/x").And.Contain("1001");
    }

    [Fact]
    public void A_refusal_answers_an_empty_mount_never_null()
    {
        Judge("relative/folder").Mount.Should().Be(BaseMountReport.Unknown);
    }
}
