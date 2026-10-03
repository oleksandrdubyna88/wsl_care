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

    [Fact]
    public void The_cap_counts_bytes_read_never_the_length_a_stream_claims()
    {
        using var lying = new LyingStream(claimedLength: 10, actualBytes: 2 * 1024 * 1024);

        RegularFiles.Capped(lying, 1024).Should().BeOfType<FileReadResult.Unreadable>().Which.Reason.Should().Contain("larger than 1024 bytes");
        lying.Served.Should().BeLessThanOrEqualTo(1025, "nothing past the cap is read");
    }

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
