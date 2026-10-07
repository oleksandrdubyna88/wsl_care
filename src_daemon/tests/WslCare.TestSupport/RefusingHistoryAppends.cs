using WslCare.Core.Records;

namespace WslCare.TestSupport;

/// <summary>A file system whose appends to <c>history.jsonl</c> fail — with <c>the history file is on a disk that went away
/// (test)</c> unless the test names its own exception (a <see cref="TimeoutException"/> is what a history lock that cannot be
/// taken throws); every other write is the real one. A run then reaches its end and cannot write its line.</summary>
public sealed class RefusingHistoryAppends(Core.Files.IFileSystem inner, Func<Exception>? failure = null) : DelegatingFileSystem(inner)
{
    public override void AppendLine(string path, string line, TimeSpan lockTimeout)
    {
        if (path.EndsWith(RunRecordWriter.FileName, StringComparison.Ordinal))
        {
            throw (failure ?? (static () => new IOException("the history file is on a disk that went away (test)")))();
        }

        base.AppendLine(path, line, lockTimeout);
    }
}
