using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Processes;

namespace WslCare.Core.Tests.Processes;

/// <summary>
/// Where the distro sees the Windows system drive (<see cref="WindowsSystemDrive.Mount"/>, over <c>/proc/self/mountinfo</c>)
/// and whether WSL interop can run a Windows program (<see cref="WindowsSystemDrive.InteropRefusal"/>) — one fixture per
/// shape the rule must take or refuse. Pure text: these run on every leg. The resolver's use of both is
/// <see cref="SystemDriveResolverTests"/>.
/// </summary>
public sealed class WindowsSystemDriveTests
{
    /// <summary>The system drive's line of <c>/proc/self/mountinfo</c> on the owner's machine, 2026-10-04 (WSL 2.7.10,
    /// kernel 6.18), UNEDITED — it holds no user data (<c>uid=1000</c> is the default account's number, not a name).</summary>
    private const string ObservedC =
        @"487 523 0:159 / /mnt/c rw,noatime - 9p C:\134 rw,aname=drvfs;path=C:\;uid=1000;gid=1000;symlinkroot=/mnt/,cache=0x5,access=client,msize=65536,trans=fd,rfd=6,wfd=6";

    /// <summary>The other lines of that table a C: rule must NOT take: the root filesystem, WSL's driver share, a mapped
    /// network drive, two fixed drives (one stacked twice, as Docker Desktop leaves it), and Docker Desktop's mount of a
    /// FOLDER of <c>C:</c>.</summary>
    private const string ObservedOthers =
        "523 504 8:96 / / rw,relatime - ext4 /dev/sdg rw,discard,errors=remount-ro,data=ordered\n" +
        "520 523 0:36 / /usr/lib/wsl/drivers ro,nosuid,nodev,noatime - 9p drivers ro,aname=drivers;fmask=222;dmask=222,cache=0x5,access=client,msize=65536,trans=fd,rfd=8,wfd=8\n" +
        "479 523 0:154 / /mnt/v rw,relatime - 9p V: rw,aname=drvfs;path=V:;uid=1000;gid=1000;metadata;symlinkroot=/mnt/,cache=0x5,access=client,msize=65536,trans=fd,rfd=3,wfd=3\n" +
        @"483 523 0:157 / /mnt/d rw,noatime - 9p D:\134 rw,aname=drvfs;path=D:\;uid=1000;gid=1000;symlinkroot=/mnt/,cache=0x5,access=client,msize=65536,trans=fd,rfd=6,wfd=6" + "\n" +
        @"488 523 0:161 / /mnt/f rw,noatime - 9p F:\134 rw,aname=drvfs;path=F:\;uid=1000;gid=1000;symlinkroot=/mnt/,cache=0x5,access=client,msize=65536,trans=fd,rfd=6,wfd=6" + "\n" +
        @"1560 523 0:190 / /Docker/host rw,noatime - 9p C:\134Program\040Files\134Docker\134Docker\134resources rw,aname=drvfs;path=C:\Program Files\Docker\Docker\resources;symlinkroot=/mnt/,cache=0x5,access=client,msize=65536,trans=fd,rfd=3,wfd=3" + "\n" +
        @"945 483 0:157 / /mnt/d rw,noatime - 9p D:\134 rw,aname=drvfs;path=D:\;uid=1000;gid=1000;symlinkroot=/mnt/,cache=0x5,access=client,msize=65536,trans=fd,rfd=6,wfd=6" + "\n";

    private const string Interop = "enabled\ninterpreter /init\nflags: P\noffset 0\nmagic 4d5a\n";

    private static Reading<SystemDriveMount> MountOf(string mountInfo) => WindowsSystemDrive.Mount(mountInfo, "/mnt/");

    private static string Refusal(string mountInfo) =>
        MountOf(mountInfo).Should().BeOfType<Reading<SystemDriveMount>.Unavailable>().Which.Reason;

    [Fact]
    public void The_observed_wsl2_table_names_the_system_drive_and_its_device()
    {
        MountOf(ObservedOthers + ObservedC + "\n").Should().Be(Reading.Of(new SystemDriveMount("/mnt/c", 0, 159)));
    }

    [Fact]
    public void A_wsl1_drvfs_line_with_the_drive_root_or_the_bare_drive_as_its_source_is_the_drive()
    {
        // Constructed: WSL 1 names the type drvfs and has no path= option (not observed on this machine).
        MountOf(@"40 30 0:42 / /mnt/c rw,noatime - drvfs C:\134 rw,uid=1000").Should().Be(Reading.Of(new SystemDriveMount("/mnt/c", 0, 42)));
        MountOf("40 30 0:42 / /mnt/c rw,noatime - drvfs C: rw,uid=1000").Should().Be(Reading.Of(new SystemDriveMount("/mnt/c", 0, 42)));
    }

    [Fact]
    public void A_manual_drvfs_mount_of_C_colon_is_the_drive_by_its_path_option()
    {
        // `mount -t drvfs C: /mnt/c` writes path=C: (no backslash) — the shape of the mapped drive V: above.
        MountOf("500 523 0:170 / /mnt/c rw,relatime - 9p C: rw,aname=drvfs;path=C:;uid=1000;gid=1000").Should().Be(Reading.Of(new SystemDriveMount("/mnt/c", 0, 170)));
    }

    [Fact]
    public void A_9p_line_is_judged_by_its_path_option_never_by_its_source_label()
    {
        Refusal(@"501 523 0:171 / /mnt/c rw,noatime - 9p C:\134 rw,aname=drvfs;path=D:\;uid=1000").Should().Contain(@"C:\");
        Refusal(@"502 523 0:172 / /mnt/c rw,noatime - 9p C:\134 rw,aname=other;path=C:\").Should().Contain(@"C:\");
    }

    [Fact]
    public void An_automount_root_holding_a_space_is_decoded_from_the_kernels_escape()
    {
        MountOf(ObservedC.Replace(" /mnt/c ", @" /my\040drives/c ", StringComparison.Ordinal))
            .Should().Be(Reading.Of(new SystemDriveMount("/my drives/c", 0, 159)));
    }

    [Fact]
    public void Two_lines_for_one_mount_point_agree_and_the_one_on_top_gives_the_device()
    {
        var stacked = ObservedC.Replace("487 523 0:159", "900 487 0:200", StringComparison.Ordinal);

        MountOf(ObservedC + "\n" + stacked).Should().Be(Reading.Of(new SystemDriveMount("/mnt/c", 0, 200)));
    }

    [Fact]
    public void Two_different_mount_points_of_the_drive_are_refused_naming_both()
    {
        var second = ObservedC.Replace("487 523 0:159 / /mnt/c ", "901 523 0:202 / /mnt/wsl/docker-desktop-bind-mounts/Ubuntu/0123abcd ", StringComparison.Ordinal);

        Refusal(ObservedC + "\n" + second).Should().Contain("/mnt/c").And.Contain("/mnt/wsl/docker-desktop-bind-mounts/Ubuntu/0123abcd").And.Contain("not guessed");
    }

    [Fact]
    public void A_bind_of_a_folder_of_the_drive_is_not_the_drive()
    {
        var folder = ObservedC.Replace("487 523 0:159 / /mnt/c ", "902 523 0:159 /Windows /srv/win ", StringComparison.Ordinal);

        MountOf(folder + "\n" + ObservedC).Should().Be(Reading.Of(new SystemDriveMount("/mnt/c", 0, 159)));
        Refusal(folder).Should().Contain("nothing is mounted at /mnt/c");
    }

    [Fact]
    public void No_mount_of_the_drive_is_refused_naming_what_was_looked_for()
    {
        Refusal(ObservedOthers).Should().Contain(WindowsSystemDrive.MountInfo).And.Contain(@"C:\").And.Contain("nothing is mounted at /mnt/c");
        Refusal(string.Empty).Should().Contain(@"C:\");
        Refusal("not a mountinfo line at all\n1 2 3\n").Should().Contain(@"C:\");
    }

    [Fact]
    public void WSLs_virtiofs_mode_is_not_identified_and_the_refusal_names_the_type_found_at_the_drive_folder()
    {
        // Constructed from WSL's drvfs mount code (the share mounted by TAG, a child of it bound onto the drive's folder) —
        // not observed on this machine, which mounts drives over 9p.
        const string Virtiofs =
            "600 523 0:210 / /mnt/wsl/drvfs-shares rw,relatime - virtiofs drvfsTag rw\n" +
            "601 523 0:210 /C /mnt/c rw,relatime - virtiofs drvfsTag rw\n";

        Refusal(Virtiofs).Should().Contain("/mnt/c is a virtiofs mount of drvfsTag (root /C)");
    }

    [Fact]
    public void A_relative_mount_point_is_never_used()
    {
        Refusal(ObservedC.Replace(" /mnt/c ", " mnt/c ", StringComparison.Ordinal)).Should().Contain(@"C:\");
    }

    [Fact]
    public void Interop_registered_and_enabled_under_either_name_lets_a_windows_program_run()
    {
        WindowsSystemDrive.InteropRefusal(e => e.EndsWith("/WSLInterop", StringComparison.Ordinal) ? Reading.Of(Interop) : Reading.Missing<string>($"{e} does not exist"))
            .Should().BeEmpty();
        WindowsSystemDrive.InteropRefusal(e => e.EndsWith("-late", StringComparison.Ordinal) ? Reading.Of(Interop) : Reading.Missing<string>($"{e} does not exist"))
            .Should().BeEmpty();
    }

    [Fact]
    public void Interop_disabled_or_not_registered_is_a_refusal_naming_interop()
    {
        WindowsSystemDrive.InteropRefusal(e => e.EndsWith("/WSLInterop", StringComparison.Ordinal) ? Reading.Of(Interop.Replace("enabled", "disabled", StringComparison.Ordinal)) : Reading.Missing<string>($"{e} does not exist"))
            .Should().Contain("not enabled").And.Contain("disabled");
        WindowsSystemDrive.InteropRefusal(e => Reading.Missing<string>($"{e} does not exist"))
            .Should().Contain("not registered").And.Contain("WSLInterop-late");
    }
}
