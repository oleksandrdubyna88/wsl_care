namespace WslCare.Cli.Tests;

/// <summary>A stdin whose writer never closes: every read blocks until the test ends.</summary>
internal sealed class NeverEndingStream : Stream
{
    private readonly ManualResetEventSlim _released = new();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        _released.Wait();
        return 0;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _released.Set();
        if (disposing)
        {
            _released.Dispose();
        }

        base.Dispose(disposing);
    }
}
