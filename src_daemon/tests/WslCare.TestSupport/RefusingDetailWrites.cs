using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Records;

namespace WslCare.TestSupport;

/// <summary>A file system whose run-detail writes fail with <c>disk full (test)</c>; everything else is the real one — a run
/// then records itself <c>failed</c> on a line that names no detail.</summary>
public sealed class RefusingDetailWrites(IFileSystem inner) : DelegatingFileSystem(inner)
{
    public override DeletionVerdict WriteFileAtomically(string path, ReadOnlySpan<byte> content, DeletionScope scope) =>
        path.Contains(RunDetailStore.Folder, StringComparison.Ordinal) ? throw new IOException("disk full (test)") : base.WriteFileAtomically(path, content, scope);
}
