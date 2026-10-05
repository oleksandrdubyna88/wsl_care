using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;

namespace WslCare.TestSupport;

/// <summary>
/// An <see cref="IFileSystem"/> that hands every call to <paramref name="inner"/> — the base of every test double
/// that changes ONE answer of the real file system (a refused write, a link table, a failing append), so a member
/// added to the seam is delegated once here rather than in every double.
/// </summary>
public class DelegatingFileSystem(IFileSystem inner) : IFileSystem
{
    public virtual FileReadResult ReadFile(string path) => inner.ReadFile(path);

    public virtual FileReadResult ReadRegularFile(string path, int maxBytes) => inner.ReadRegularFile(path, maxBytes);

    public virtual FileReadResult ReadStateFile(string path, int maxBytes) => inner.ReadStateFile(path, maxBytes);

    public virtual FileReadResult ReadUserFile(string path, int maxBytes, uint owner) => inner.ReadUserFile(path, maxBytes, owner);

    public virtual FileReadResult ReadNoFollowFile(string path, int maxBytes) => inner.ReadNoFollowFile(path, maxBytes);

    public virtual bool FileExists(string path) => inner.FileExists(path);

    public virtual bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public virtual IReadOnlyList<string> ListDirectories(string path) => inner.ListDirectories(path);

    public virtual LinkReadResult ReadLink(string path) => inner.ReadLink(path);

    public virtual VolumeReadResult MeasureVolume(string path) => inner.MeasureVolume(path);

    public virtual FileSizeResult FileSize(string path) => inner.FileSize(path);

    public virtual DirectoryTimeResult DirectoryLastWrite(string path) => inner.DirectoryLastWrite(path);

    public virtual IReadOnlyList<string> ListFiles(string path) => inner.ListFiles(path);

    public virtual TreeMeasure MeasureTree(string path, TreeLimits limits, IReadOnlySet<string> countOnlyUnder, IReadOnlySet<string> neverEnter, CancellationToken cancellationToken) =>
        inner.MeasureTree(path, limits, countOnlyUnder, neverEnter, cancellationToken);

    public virtual WriteAccess ProbeWriteAccess(string directory) => inner.ProbeWriteAccess(directory);

    public virtual ExclusiveLock TryLockExclusive(string lockPath) => inner.TryLockExclusive(lockPath);

    public virtual DeletionVerdict RewriteLines(string path, Func<IReadOnlyList<string>, IReadOnlyList<string>> keep, DeletionScope scope, TimeSpan lockTimeout) =>
        inner.RewriteLines(path, keep, scope, lockTimeout);

    public virtual void CreateDirectory(string path) => inner.CreateDirectory(path);

    public virtual DeletionVerdict WriteFileAtomically(string path, ReadOnlySpan<byte> content, DeletionScope scope) => inner.WriteFileAtomically(path, content, scope);

    public virtual ExclusiveCreate CreateFileExclusively(string path, ReadOnlySpan<byte> content, DeletionScope scope) => inner.CreateFileExclusively(path, content, scope);

    public virtual void AppendLine(string path, string line, TimeSpan lockTimeout) => inner.AppendLine(path, line, lockTimeout);

    public virtual DeletionVerdict DeleteFile(string path, DeletionScope scope) => inner.DeleteFile(path, scope);

    public virtual DeletionVerdict DeleteDirectory(string path, DeletionScope scope) => inner.DeleteDirectory(path, scope);

    public virtual DeletionVerdict MoveFile(string from, string to, DeletionScope scope) => inner.MoveFile(from, to, scope);

    public virtual DeletionVerdict MoveDirectory(string from, string to, DeletionScope scope) => inner.MoveDirectory(from, to, scope);
}
