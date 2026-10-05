using FluentAssertions;

using WslCare.Core.Files;
using WslCare.Core.Health;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Files;

/// <summary>
/// Plan §15q, E7.S0 review round S1: the Windows-profile reader (no owner check, because drvfs shows every file as the mount's
/// uid, 0777) must stay ON the drive: a link anywhere on the path — not only the last component — must not take root's read to a
/// file elsewhere (<c>~/…/.docker</c> → <c>/root/.docker</c>), and a profile path with a <c>..</c> must not climb out of it.
/// </summary>
public sealed class DriveReaderTests : IDisposable
{
    private readonly TempRoot _root = new("drive-reader");

    public void Dispose() => _root.Dispose();

    [Fact]
    public void A_link_in_a_parent_folder_of_a_drive_file_is_never_followed()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a folder link needs no privilege on Linux; run in WSL or on the Linux legs");
        var secret = _root.File("root-only/.docker/daemon.json", """{ "secret": true }""");
        var profile = _root.Dir("mnt/c/Users/me");
        Directory.CreateSymbolicLink(Path.Combine(profile, ".docker"), Path.GetDirectoryName(secret)!);
        var files = new PhysicalFileSystem(ProcfsFixture.PathsAt(_root.Path));

        var read = files.ReadNoFollowFile(Path.Combine(profile, ".docker", "daemon.json"), 1024);

        read.Should().BeOfType<FileReadResult.Unreadable>().Which.Reason.Should().Contain("link");
    }

    [Fact]
    public void A_plain_drive_file_is_still_read()
    {
        var file = _root.File("mnt/c/Users/me/.wslconfig", "[wsl2]\n");
        var files = new PhysicalFileSystem(ProcfsFixture.PathsAt(_root.Path));

        files.ReadNoFollowFile(file, 1024).Should().BeOfType<FileReadResult.Content>();
    }

    [Theory]
    [InlineData(@"C:\..\..\root")]
    [InlineData(@"C:\Users\me\..\..\..\root")]
    [InlineData("C:/Users/../../etc")]
    public void A_windows_profile_with_a_parent_segment_is_not_a_path_on_the_drive(string profile) =>
        HealthCollector.InDistro(profile, "/mnt/").IsAvailable.Should().BeFalse();
}
