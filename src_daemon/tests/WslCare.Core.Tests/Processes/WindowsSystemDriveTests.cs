using System.Text;

using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Health;
using WslCare.Core.Processes;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Processes;

/// <summary>
/// The Windows programs the product starts from inside the distro (the clock probe's <c>powershell.exe</c>) are found
/// on the mounted Windows system drive when <c>PATH</c> does not name them — which is EVERY run under systemd: a service
/// gets <c>/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/snap/bin</c>, and WSL appends the Windows folders to
/// <c>PATH</c> only for interactive and login sessions. Observed live 2026-10-04 on the first install: under
/// <c>wsl-care.service</c> <c>clock.drift</c> was unknown and A16 refused, both with "powershell.exe was not found on
/// PATH (5 directories searched, executable files only)". Real files in a temporary root, a mount table in the shape
/// the WSL 2 kernel prints.
/// </summary>
public sealed class WindowsSystemDriveTests : IDisposable
{
    /// <summary>The <c>PATH</c> systemd hands <c>wsl-care.service</c> (no <c>Environment=PATH</c> in the unit).</summary>
    private const string ServicePath = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/snap/bin";

    /// <summary>
    /// <c>/proc/self/mounts</c> of a WSL 2 distro as observed 2026-10-04 (kernel 6.18, Docker Desktop running): the
    /// driver share, a mapped network drive, three fixed drives, a folder of <c>C:</c> Docker mounts, and Docker
    /// Desktop's second mount of <c>D:\</c> (its folder name, a hash, replaced). <c>{c}</c> is where <c>C:\</c> is mounted.
    /// </summary>
    private const string ObservedMountTable =
        "drivers /usr/lib/wsl/drivers 9p ro,nosuid,nodev,noatime,aname=drivers;fmask=222;dmask=222,cache=0x5,access=client,msize=65536,trans=fd,rfd=8,wfd=8 0 0\n" +
        "V: /mnt/v 9p rw,relatime,aname=drvfs;path=V:;uid=1000;gid=1000;metadata;symlinkroot=/mnt/,cache=0x5,access=client,msize=65536,trans=fd,rfd=3,wfd=3 0 0\n" +
        "C:\\134 {c} 9p rw,noatime,aname=drvfs;path=C:\\;uid=1000;gid=1000;symlinkroot=/mnt/,cache=0x5,access=client,msize=65536,trans=fd,rfd=6,wfd=6 0 0\n" +
        "D:\\134 /mnt/d 9p rw,noatime,aname=drvfs;path=D:\\;uid=1000;gid=1000;symlinkroot=/mnt/,cache=0x5,access=client,msize=65536,trans=fd,rfd=6,wfd=6 0 0\n" +
        "F:\\134 /mnt/f 9p rw,noatime,aname=drvfs;path=F:\\;uid=1000;gid=1000;symlinkroot=/mnt/,cache=0x5,access=client,msize=65536,trans=fd,rfd=6,wfd=6 0 0\n" +
        "C:\\134Program\\040Files\\134Docker\\134Docker\\134resources /Docker/host 9p rw,noatime,aname=drvfs;path=C:\\Program Files\\Docker\\Docker\\resources;symlinkroot=/mnt/,cache=0x5,access=client,msize=65536,trans=fd,rfd=3,wfd=3 0 0\n" +
        "D:\\134 /mnt/wsl/docker-desktop-bind-mounts/Ubuntu/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef 9p rw,noatime,aname=drvfs;path=D:\\;uid=1000;gid=1000;symlinkroot=/mnt/,cache=0x5,access=client,msize=65536,trans=fd,rfd=6,wfd=6 0 0\n";

    private readonly TempRoot _root = new("system-drive");

    public void Dispose() => _root.Dispose();

    /// <summary>The program's folder as the PRODUCT names it, read back rather than retyped.</summary>
    private static string PowerShellFolder => WindowsSystemDrive.Programs[HealthCommands.PowerShell];

    /// <summary>The observed table, with <c>C:\</c> mounted at <paramref name="mountPoint"/> (escaped as the kernel escapes it).</summary>
    private static string TableWithC(string mountPoint) => ObservedMountTable.Replace("{c}", Escaped(mountPoint), StringComparison.Ordinal);

    /// <summary>The kernel's escaping of a mount table field: space, tab, newline and backslash as three octal digits.</summary>
    private static string Escaped(string field) =>
        field.Replace("\\", "\\134", StringComparison.Ordinal).Replace(" ", "\\040", StringComparison.Ordinal).Replace("\t", "\\011", StringComparison.Ordinal).Replace("\n", "\\012", StringComparison.Ordinal);

    /// <summary>A mounted system drive holding <c>powershell.exe</c> where Windows installs it; the program's full path.</summary>
    private string PlantPowerShell(string drive, string content = "MZ\u0090\0 a Windows program, as far as the header says", bool executable = true)
    {
        var path = _root.File($"{drive}/{PowerShellFolder}/{HealthCommands.PowerShell}", content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, executable
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return path;
    }

    private Func<Reading<string>> MountedAt(string drive) => () => WindowsSystemDrive.MountPoint(TableWithC(_root.Under(drive)));

    private static string FoundPath(ResolvedExecutable resolved) =>
        Path.GetFullPath(resolved.Should().BeOfType<ResolvedExecutable.Found>("the resolver answered {0}", resolved).Which.Path);

    [Fact]
    public void Under_a_services_minimal_path_the_windows_clock_probe_still_resolves_powershell_on_the_mounted_system_drive()
    {
        var planted = PlantPowerShell("mnt/c");

        var resolved = ExecutableResolver.Resolve(HealthCommands.PowerShell, ServicePath, windows: false, MountedAt("mnt/c"));

        FoundPath(resolved).Should().Be(Path.GetFullPath(planted));
    }

    [Fact]
    public void A_powershell_on_path_still_wins_over_the_system_drive()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a Linux PATH (split at ':') cannot hold this machine's drive-letter temp path");
        PlantPowerShell("mnt/c");
        var onPath = PlantPowerShell("interop-path");

        var resolved = ExecutableResolver.Resolve(HealthCommands.PowerShell, _root.Under($"interop-path/{PowerShellFolder}"), windows: false, MountedAt("mnt/c"));

        FoundPath(resolved).Should().Be(Path.GetFullPath(onPath));
    }

    [Fact]
    public void The_system_drive_is_searched_only_for_the_windows_programs_the_product_names()
    {
        var other = _root.File($"mnt/c/{PowerShellFolder}/wc-tool", "MZ");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(other, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var asked = 0;
        Reading<string> Drive()
        {
            asked++;
            return WindowsSystemDrive.MountPoint(TableWithC(_root.Under("mnt/c")));
        }

        var resolved = ExecutableResolver.Resolve("wc-tool", ServicePath, windows: false, Drive);

        resolved.Should().BeOfType<ResolvedExecutable.NotFound>().Which.Reason.Should().NotContain("system drive");
        asked.Should().Be(0, "the mount table is read only for a program the product starts from the system drive");
    }

    [Fact]
    public void A_file_there_that_is_not_a_windows_program_is_never_started()
    {
        // Under systemd the run is root: an ELF or a script planted under that name would run NATIVELY, as root. A file
        // with the MZ header can only run through WSL interop — on Windows, as the Windows user.
        PlantPowerShell("mnt/c", content: "\u007fELF not a Windows program");

        var resolved = ExecutableResolver.Resolve(HealthCommands.PowerShell, ServicePath, windows: false, MountedAt("mnt/c"));

        var reason = resolved.Should().BeOfType<ResolvedExecutable.NotFound>().Which.Reason;
        reason.Should().Contain("not a Windows program").And.Contain("PATH");
    }

    [Fact]
    public void A_symbolic_link_there_is_never_followed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "creating a symbolic link needs a privilege on Windows; the rule is the distro's");
        var elsewhere = PlantPowerShell("elsewhere");
        var link = _root.Under($"mnt/c/{PowerShellFolder}/{HealthCommands.PowerShell}");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, elsewhere);

        var resolved = ExecutableResolver.Resolve(HealthCommands.PowerShell, ServicePath, windows: false, MountedAt("mnt/c"));

        resolved.Should().BeOfType<ResolvedExecutable.NotFound>().Which.Reason.Should().Contain("symbolic link");
    }

    [Fact]
    public void On_linux_a_copy_without_an_execute_bit_is_not_started()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the execute bit is a Linux rule");
        PlantPowerShell("mnt/c", executable: false);

        var resolved = ExecutableResolver.Resolve(HealthCommands.PowerShell, ServicePath, windows: false, MountedAt("mnt/c"));

        resolved.Should().BeOfType<ResolvedExecutable.NotFound>().Which.Reason.Should().Contain("execute bit");
    }

    [Fact]
    public void When_the_system_drive_is_mounted_but_holds_no_powershell_the_reason_names_the_path_it_checked()
    {
        _root.Dir("mnt/c");

        var resolved = ExecutableResolver.Resolve(HealthCommands.PowerShell, ServicePath, windows: false, MountedAt("mnt/c"));

        var reason = resolved.Should().BeOfType<ResolvedExecutable.NotFound>().Which.Reason;
        reason.Should().Contain("PATH").And.Contain(HealthCommands.PowerShell).And.Contain("does not exist");
    }

    [Fact]
    public void When_no_system_drive_is_mounted_the_reason_says_PATH_was_searched_and_the_drive_was_not_found()
    {
        var resolved = ExecutableResolver.Resolve(
            HealthCommands.PowerShell, ServicePath, windows: false, () => WindowsSystemDrive.MountPoint("drivers /usr/lib/wsl/drivers 9p ro,aname=drivers 0 0\n"));

        var reason = resolved.Should().BeOfType<ResolvedExecutable.NotFound>().Which.Reason;
        reason.Should().Contain("PATH").And.Contain(@"C:\");
    }

    [Fact]
    public void On_windows_the_system_drive_is_never_consulted()
    {
        PlantPowerShell("mnt/c");
        var asked = 0;

        var resolved = ExecutableResolver.Resolve(HealthCommands.PowerShell, _root.Under("empty"), windows: true, () =>
        {
            asked++;
            return Reading.Of(_root.Under("mnt/c"));
        });

        resolved.Should().BeOfType<ResolvedExecutable.NotFound>();
        asked.Should().Be(0, "on Windows powershell.exe is System32's, on PATH; the fallback is the distro's");
    }

    [Fact]
    public void The_mount_table_of_a_wsl_distro_names_where_the_system_drive_is_mounted()
    {
        WindowsSystemDrive.MountPoint(TableWithC("/mnt/c")).Should().Be(Reading.Of("/mnt/c"));
    }

    [Fact]
    public void A_custom_automount_root_from_wsl_conf_is_read_from_where_wsl_mounted_the_drive()
    {
        // [automount] root=/windir/ in /etc/wsl.conf is applied by WSL when it mounts; the mount table is that answer.
        WindowsSystemDrive.MountPoint(TableWithC("/windir/c")).Should().Be(Reading.Of("/windir/c"));
        WindowsSystemDrive.MountPoint(TableWithC("/my drives/c")).Should().Be(Reading.Of("/my drives/c"), "the kernel escapes a space as \\040");
    }

    [Fact]
    public void A_folder_of_c_a_network_drive_and_a_non_drvfs_share_are_not_the_system_drive()
    {
        var withoutC = string.Join('\n', ObservedMountTable.Split('\n').Where(l => !l.Contains("{c}", StringComparison.Ordinal)));
        const string NotDrvfs = "C:\\134 /mnt/c 9p rw,noatime,aname=other;path=C:\\ 0 0\n";

        WindowsSystemDrive.MountPoint(withoutC).Should().BeOfType<Reading<string>.Unavailable>().Which.Reason.Should().Contain(@"C:\");
        WindowsSystemDrive.MountPoint(NotDrvfs).Should().BeOfType<Reading<string>.Unavailable>();
        WindowsSystemDrive.MountPoint(string.Empty).Should().BeOfType<Reading<string>.Unavailable>();
    }

    [Fact]
    public void A_relative_mount_point_is_never_used()
    {
        WindowsSystemDrive.MountPoint("C:\\134 mnt/c 9p rw,aname=drvfs;path=C:\\ 0 0\n").Should().BeOfType<Reading<string>.Unavailable>();
    }

    [Fact]
    public void A_drvfs_filesystem_type_is_a_drive_too()
    {
        // `mount -t drvfs` names its type drvfs where the kernel does not translate it to 9p (WSL 1) — not observed here.
        WindowsSystemDrive.MountPoint("C:\\134 /mnt/c drvfs rw,noatime,uid=1000 0 0\n").Should().Be(Reading.Of("/mnt/c"));
    }

    [Fact]
    public void The_planted_header_fixture_is_what_the_product_reads_as_a_windows_program()
    {
        // The positive control for the refusals above: the fixture every "found" test plants carries the two bytes the
        // check reads, so a refusal test fails for ITS reason, not because every fixture is refused.
        var planted = PlantPowerShell("mnt/c");

        File.ReadAllBytes(planted).Take(2).Should().Equal(Encoding.ASCII.GetBytes("MZ"));
        ExecutableResolver.Resolve(HealthCommands.PowerShell, ServicePath, windows: false, MountedAt("mnt/c")).Should().BeOfType<ResolvedExecutable.Found>();
    }
}
