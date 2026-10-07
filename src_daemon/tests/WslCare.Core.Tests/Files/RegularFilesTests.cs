using FluentAssertions;

using WslCare.Core.Files;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Files;

/// <summary>
/// <see cref="RegularFiles"/> (independent review of E3, item 5): a file a caller named is read only when it is a REGULAR
/// file and only up to the cap of bytes actually read — a directory, a FIFO or a device is refused at once, a larger file
/// is refused whatever its length claims.
/// </summary>
public sealed class RegularFilesTests : IDisposable
{
    private readonly TempRoot _root = new("regular-files");

    public void Dispose() => _root.Dispose();

    [Fact]
    public void A_regular_file_is_read_up_to_the_cap_and_one_byte_more_is_refused()
    {
        var atCap = _root.File("at-cap.txt", new string('a', 64));
        var over = _root.File("over.txt", new string('a', 65));

        RegularFiles.Read(atCap, 64).Should().BeOfType<FileReadResult.Content>().Which.Bytes.Should().HaveCount(64);
        RegularFiles.Read(over, 64).Should().BeOfType<FileReadResult.Unreadable>().Which.Reason.Should().Contain("larger than 64 bytes");
        RegularFiles.Read(Path.Combine(_root.Path, "absent.txt"), 64).Should().BeOfType<FileReadResult.Missing>();
    }

    /// <summary>PR #10 retro round, own O4: the head read of a file on a 9p share the host serves can fail (EIO) — that is a
    /// reason the file is not started, never an exception that ends the whole collect.</summary>
    [Fact]
    public void A_head_read_that_fails_is_a_reason_naming_the_file_never_an_exception()
    {
        var status = new FileStatus(0x8000, 0x16D, 1000, 41, 0, 159);
        const string File = "/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe";

        RegularFiles.Head(File, status, 2, _ => throw new IOException("Input/output error"))
            .Should().BeOfType<Core.Collectors.Reading<FileHead>.Unavailable>().Which.Reason.Should().Contain(File).And.Contain("Input/output error");
        RegularFiles.Head(File, status, 2, _ => throw new UnauthorizedAccessException("Access to the path is denied."))
            .Should().BeOfType<Core.Collectors.Reading<FileHead>.Unavailable>().Which.Reason.Should().Contain(File).And.Contain("denied");
        RegularFiles.Head(File, status, 2, bytes => { bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; return 2; })
            .Should().BeOfType<Core.Collectors.Reading<FileHead>.Available>("a read that answers is the head").Which.Value.Bytes.Should().Equal("MZ"u8.ToArray());
    }

    [Fact]
    public void The_cap_counts_bytes_read_never_the_length_a_stream_claims()
    {
        using var lying = new LyingStream(claimedLength: 10, actualBytes: 2 * 1024 * 1024);

        RegularFiles.Capped(lying, 1024).Should().BeOfType<FileReadResult.Unreadable>().Which.Reason.Should().Contain("larger than 1024 bytes");
        lying.Served.Should().BeLessThanOrEqualTo(1025, "nothing past the cap is read");
    }

    /// <summary>E6.S0 review S1: a state file another process trusts is the owner's alone — another uid, or a group / other
    /// write bit, is a refusal naming which.</summary>
    [Theory]
    [InlineData(0u, 0x81A4, 0u, "")]
    [InlineData(1000u, 0x81A4, 0u, "owned by uid 1000, not uid 0")]
    [InlineData(0u, 0x81B4, 0u, "writable by group or others (mode 664)")]
    [InlineData(0u, 0x81A6, 0u, "writable by group or others (mode 646)")]
    public void A_state_file_is_trusted_only_when_its_owner_alone_may_write_it(uint fileOwner, int mode, uint owner, string problem) =>
        RegularFiles.OwnershipProblem(fileOwner, mode, owner).Should().Be(problem);

    [Fact]
    public void A_directory_is_refused_as_not_a_regular_file()
    {
        var folder = _root.Dir("folder");

        RegularFiles.Read(folder, 64).Should().BeOfType<FileReadResult.Unreadable>().Which.Reason.Should().Contain(RegularFiles.NotRegular);
    }

    [Fact]
    public async Task A_fifo_and_a_character_device_are_refused_at_once_never_waited_on_nor_streamed()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "FIFOs and /dev/zero are Linux files: run in WSL or on the Linux legs");
        var fifo = Path.Combine(_root.Path, "shown.fifo");
        (await ChildProcess.RunAsync("mkfifo", [fifo], new Dictionary<string, string?>())).Exit.Should().Be(0);

        var read = Task.Run(() => (RegularFiles.Read(fifo, 64), RegularFiles.Read("/dev/zero", 64)), TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)) == read;
        if (!finished)
        {
            await using var writer = new FileStream(fifo, FileMode.Open, FileAccess.Write); // releases the blocked reader
        }

        finished.Should().BeTrue("an open that could block is never made");
        var (fromFifo, fromDevice) = await read;
        fromFifo.Should().BeOfType<FileReadResult.Unreadable>().Which.Reason.Should().Contain("a FIFO");
        fromDevice.Should().BeOfType<FileReadResult.Unreadable>().Which.Reason.Should().Contain("a device");
    }

    /// <summary>A stream whose <see cref="Length"/> says one thing and whose reads say another.</summary>
    private sealed class LyingStream(long claimedLength, int actualBytes) : Stream
    {
        public int Served { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => claimedLength;

        public override long Position { get => Served; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(count, actualBytes - Served);
            Served += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
