using FluentAssertions;

using WslCare.Core.Files;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Files;

/// <summary>
/// <see cref="RegularFiles.ReadNoFollow"/> (plan §15q R1.1, review M2): a Windows-profile file read through drvfs — where every
/// file shows the mount's uid and mode 0777 — is read without an owner or mode check, but still never through a link, never
/// waited on, and never past its cap.
/// </summary>
public sealed class NoFollowReaderTests : IDisposable
{
    private readonly TempRoot _root = new("no-follow-reader");

    public void Dispose() => _root.Dispose();

    [Fact]
    public void A_world_writable_file_is_read_because_drvfs_reports_every_file_that_way()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "file modes are Linux's: run in WSL or on the Linux legs");
        var file = _root.File(".wslconfig", "[wsl2]\nmemory=36GB\n");
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(file, (UnixFileMode)0b111_111_111);
        }

        RegularFiles.ReadNoFollow(file, 1024).Should().BeOfType<FileReadResult.Content>();
        RegularFiles.ReadOwned(file, 1024, RegularFiles.EffectiveUid()).Should().BeOfType<FileReadResult.Unreadable>("the owner-checked reader refuses the same file");
    }

    [Fact]
    public async Task A_link_is_never_followed_and_a_fifo_never_waited_on()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a file link needs no privilege on Linux, and a FIFO is a Linux file");
        var fifo = Path.Combine(_root.Path, "daemon.json");
        (await ChildProcess.RunAsync("mkfifo", [fifo], new Dictionary<string, string?>())).Exit.Should().Be(0);
        var read = Task.Run(() => RegularFiles.ReadNoFollow(fifo, 64), TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)) == read;
        if (!finished)
        {
            await using var writer = new FileStream(fifo, FileMode.Open, FileAccess.Write); // releases the blocked reader
        }

        finished.Should().BeTrue("a FIFO is refused at once");
        (await read).Should().BeOfType<FileReadResult.Unreadable>().Which.Reason.Should().Contain(RegularFiles.NotRegular);
        var target = _root.File("target.json", "{}");
        var link = Path.Combine(_root.Path, "link.json");
        File.CreateSymbolicLink(link, target);

        RegularFiles.ReadNoFollow(link, 1024).Should().BeOfType<FileReadResult.Unreadable>().Which.Reason.Should().Contain("symbolic link");
        RegularFiles.ReadNoFollow(_root.Dir("folder"), 1024).Should().BeOfType<FileReadResult.Unreadable>().Which.Reason.Should().Contain(RegularFiles.NotRegular);
        RegularFiles.ReadNoFollow(_root.File("big", new string('x', 65)), 64).Should().BeOfType<FileReadResult.Unreadable>().Which.Reason.Should().Contain("larger than 64 bytes");
    }
}
