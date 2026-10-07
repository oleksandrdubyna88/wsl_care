using System.Diagnostics;

using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Files;
using WslCare.Core.Health;
using WslCare.Core.Processes;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Processes;

/// <summary>
/// What is checked before a file on the mounted system drive is started (<see cref="SystemDriveFiles"/>), over REAL files
/// in a temporary tree standing in for the drive. Linux only: statx, symbolic links, execute bits and FIFOs are the
/// distro's; on Windows every check answers that the drive is inspected only inside a distro (asserted once).
/// </summary>
public sealed class SystemDriveFilesTests : IDisposable
{
    private readonly TempRoot _root = new("system-drive-files");

    public void Dispose() => _root.Dispose();

    private static string Folder => WindowsSystemDrive.Programs[HealthCommands.PowerShell];

    private const UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>The temp tree as a "mount": its device read back from the same statx the check uses, never guessed.</summary>
    private SystemDriveMount Drive()
    {
        var point = _root.Dir("mnt/c");
        var read = OperatingSystem.IsLinux() ? RegularFiles.StatNoFollow(point) : Reading.Missing<FileStatus>("not Linux");
        var status = read.Should().BeOfType<Reading<FileStatus>.Available>().Which.Value;
        return new SystemDriveMount(point, status.DeviceMajor, status.DeviceMinor);
    }

    private string Plant(string content = "MZ\u0090\0 a Windows program, as far as the header says", UnixFileMode mode = Executable)
    {
        var path = _root.File($"mnt/c/{Folder}/{HealthCommands.PowerShell}", content);
        Chmod(path, mode);
        return path;
    }

    private static void Chmod(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
        }
    }

    private static void SkipOnWindows() => Assert.SkipWhen(OperatingSystem.IsWindows(), "statx, links, execute bits and FIFOs are the distro's");

    [Fact]
    public void A_regular_executable_file_with_the_windows_header_on_the_mount_passes_every_check()
    {
        SkipOnWindows();
        var drive = Drive();
        Plant();

        SystemDriveFiles.Problem(drive, Folder, HealthCommands.PowerShell).Should().BeEmpty();
    }

    [Fact]
    public void A_file_without_the_MZ_header_is_never_started()
    {
        // Under systemd the run is root: an ELF or a script planted under that name would run NATIVELY, as root.
        SkipOnWindows();
        var drive = Drive();
        Plant(content: "\u007fELF not a Windows program");

        SystemDriveFiles.Problem(drive, Folder, HealthCommands.PowerShell).Should().Contain("not a Windows program (no MZ header)");
    }

    [Fact]
    public void A_symbolic_link_on_any_component_below_the_mount_point_is_refused()
    {
        SkipOnWindows();
        var drive = Drive();
        var elsewhere = _root.File($"elsewhere/System32/WindowsPowerShell/v1.0/{HealthCommands.PowerShell}", "MZ");
        Chmod(elsewhere, Executable);
        _root.Dir("mnt/c/Windows");
        Directory.CreateSymbolicLink(_root.Under("mnt/c/Windows/System32"), _root.Under("elsewhere/System32"));

        SystemDriveFiles.Problem(drive, Folder, HealthCommands.PowerShell).Should().Contain("System32 is a symbolic link");
    }

    [Fact]
    public void A_symbolic_link_as_the_file_itself_is_refused()
    {
        SkipOnWindows();
        var drive = Drive();
        var elsewhere = _root.File("elsewhere/real.exe", "MZ");
        Chmod(elsewhere, Executable);
        _root.Dir($"mnt/c/{Folder}");
        File.CreateSymbolicLink(_root.Under($"mnt/c/{Folder}/{HealthCommands.PowerShell}"), elsewhere);

        SystemDriveFiles.Problem(drive, Folder, HealthCommands.PowerShell).Should().Contain($"{HealthCommands.PowerShell} is a symbolic link");
    }

    [Fact]
    public void A_missing_file_names_the_path_checked()
    {
        SkipOnWindows();
        var drive = Drive();

        SystemDriveFiles.Problem(drive, Folder, HealthCommands.PowerShell).Should().Contain("does not exist").And.Contain(drive.MountPoint);
    }

    [Fact]
    public void A_file_without_an_execute_bit_is_refused_as_exec_would_refuse_it()
    {
        SkipOnWindows();
        var drive = Drive();
        Plant(mode: UnixFileMode.UserRead | UnixFileMode.UserWrite);

        SystemDriveFiles.Problem(drive, Folder, HealthCommands.PowerShell).Should().Contain("no execute bit");
    }

    [Fact]
    public void A_fifo_under_that_name_is_refused_at_once_never_waited_on()
    {
        SkipOnWindows();
        var drive = Drive();
        _root.Dir($"mnt/c/{Folder}");
        using (var mkfifo = Process.Start("mkfifo", _root.Under($"mnt/c/{Folder}/{HealthCommands.PowerShell}")))
        {
            mkfifo.WaitForExit(10_000).Should().BeTrue();
        }

        var clock = Stopwatch.StartNew();
        var problem = SystemDriveFiles.Problem(drive, Folder, HealthCommands.PowerShell);

        problem.Should().Contain("not a regular file (a FIFO)");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "a FIFO with no writer would hold a plain open forever");
    }

    [Fact]
    public void A_file_on_another_device_than_the_drive_mount_is_refused()
    {
        SkipOnWindows();
        var real = Drive();
        Plant();
        var other = real with { DeviceMajor = real.DeviceMajor + 7, DeviceMinor = real.DeviceMinor + 7 };

        SystemDriveFiles.Problem(other, Folder, HealthCommands.PowerShell).Should().Contain("not on the drive's mount");
    }

    [Fact]
    public void Root_owned_ancestors_nobody_else_may_write_pass_and_a_world_writable_one_is_refused()
    {
        SkipOnWindows();

        // Only "/" above: the one ancestor that is root's and 755 on every Linux. (GitHub's ubuntu image ships /usr/share as
        // 777 — observed by this test's first CI run, 2026-10-04 — so a deeper path is a fact about the runner, not the rule.)
        SystemDriveFiles.MountPointRefusal("/drive-c").Should().BeEmpty("/ is root's, mode 755");
        SystemDriveFiles.MountPointRefusal("/tmp/anything/c").Should().Contain("/tmp").And.Contain("writable by its group or by others");
    }

    /// <summary>PR #10 retro round, own O2: a LINK above the mount point is refused by its own arm. <c>/proc/self</c> is a
    /// root-owned symbolic link on every Linux, below <c>/proc</c> (root's, 555) — without the arm its 0777 link mode would be
    /// refused for another reason, which is why the test names the arm's words.</summary>
    [Fact]
    public void A_link_above_the_mount_point_is_refused_as_not_a_directory()
    {
        SkipOnWindows();
        Stat("/proc/self").Should().BeOfType<Reading<FileStatus>.Available>().Which.Value
            .Should().Match<FileStatus>(s => s.IsSymbolicLink && s.OwnerUid == 0, "the fixture is a root-owned link");

        SystemDriveFiles.MountPointRefusal("/proc/self/c").Should().Contain("/proc/self, above the drive's mount point, is not a directory");
    }

    /// <summary>PR #10 retro round, own O2: an ancestor owned by an account other than root is refused naming its uid — this
    /// account's home, whose own ancestors pass (checked first, so the refusal can only be the home's).</summary>
    [Fact]
    public void An_ancestor_owned_by_another_account_than_root_is_refused_naming_its_uid()
    {
        SkipOnWindows();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var owner = Stat(home) is Reading<FileStatus>.Available { Value: { IsDirectory: true } status } ? status.OwnerUid : 0u;
        Assert.SkipWhen(owner == 0, $"{home} is not a directory another account than root owns (this run is root's, or the home is a link)");
        Assert.SkipUnless(SystemDriveFiles.MountPointRefusal(home).Length == 0, $"an ancestor of {home} is refused already: {SystemDriveFiles.MountPointRefusal(home)}");

        SystemDriveFiles.MountPointRefusal(home + "/drive/c").Should().Be(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{home}, above the drive's mount point, is owned by uid {owner}, not root"));
    }

    /// <summary>PR #10 retro round, own O1: the checks on the OPENED descriptor, over the two readings the walk and the open
    /// produce. A positive first, so the fixture is shown to pass every check it is not about.</summary>
    [Fact]
    public void The_opened_file_the_path_check_saw_on_the_mount_with_its_header_passes()
    {
        SystemDriveFiles.HeadProblem(OnDrive, Reading.Of(Program(inode: 41)), Reading.Of(new FileHead(Program(inode: 41), Mz)), DriveC).Should().BeEmpty();
    }

    [Fact]
    public void A_file_swapped_between_the_path_check_and_the_open_is_refused()
    {
        SystemDriveFiles.HeadProblem(OnDrive, Reading.Of(Program(inode: 41)), Reading.Of(new FileHead(Program(inode: 42), Mz)), DriveC)
            .Should().Be($"{OnDrive} changed between its check and its open");
    }

    [Fact]
    public void An_opened_file_off_the_drives_mount_is_refused_even_when_the_path_check_saw_that_same_file()
    {
        var elsewhere = Program(inode: 41) with { DeviceMajor = 8, DeviceMinor = 96 };

        SystemDriveFiles.HeadProblem(OnDrive, Reading.Of(elsewhere), Reading.Of(new FileHead(elsewhere, Mz)), DriveC)
            .Should().Be($"{OnDrive} is on device 8:96, not on the drive's mount (0:159)");
    }

    private static Reading<FileStatus> Stat(string path) => OperatingSystem.IsLinux() ? RegularFiles.StatNoFollow(path) : Reading.Missing<FileStatus>("not Linux");

    private static readonly SystemDriveMount DriveC = new("/mnt/c", 0, 159);

    private static readonly byte[] Mz = "MZ"u8.ToArray();

    private static string OnDrive => $"/mnt/c/{Folder}/{HealthCommands.PowerShell}";

    /// <summary>A regular file, mode 0555 (what drvfs reports, observed 2026-10-04), on the drive's device.</summary>
    private static FileStatus Program(ulong inode) => new(0x8000, 0x16D, 1000, inode, DriveC.DeviceMajor, DriveC.DeviceMinor);

    [Fact]
    public void On_windows_the_checks_answer_that_the_drive_is_the_distros()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows answer");

        SystemDriveFiles.Problem(new SystemDriveMount(_root.Path, 0, 0), Folder, HealthCommands.PowerShell).Should().Contain("only inside a Linux distro");
        SystemDriveFiles.MountPointRefusal(_root.Path).Should().Contain("only inside a Linux distro");
    }
}
