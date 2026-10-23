using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;

namespace WslCare.TestSupport;

/// <summary>The real file system, except that writing <c>volumes.json</c> (E19.S2, the Windows watch's native volume sample) fails with
/// <paramref name="failure"/> — every other write, the ledger's and the retention sweep's included, goes through.</summary>
public sealed class FailingVolumeWrites(IFileSystem inner, Exception failure) : DelegatingFileSystem(inner)
{
    private const string FileName = "volumes.json";

    public override DeletionVerdict WriteFileAtomically(string path, ReadOnlySpan<byte> content, DeletionScope scope) =>
        path.EndsWith(FileName, StringComparison.Ordinal) ? throw failure : base.WriteFileAtomically(path, content, scope);

    public override DeletionVerdict WritePrivateFileAtomically(string path, ReadOnlySpan<byte> content, DeletionScope scope) =>
        path.EndsWith(FileName, StringComparison.Ordinal) ? throw failure : base.WritePrivateFileAtomically(path, content, scope);
}
