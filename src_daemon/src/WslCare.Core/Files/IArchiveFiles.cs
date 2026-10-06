using WslCare.Core.Files.Deletion;

namespace WslCare.Core.Files;

/// <summary>A session file opened for copying (<see cref="IArchiveFiles.OpenSource"/>) — a closed set.</summary>
public abstract record SourceOpen
{
    private SourceOpen()
    {
    }

    /// <summary>A regular file of this account with ONE link, reached through no link; the caller disposes the stream.</summary>
    public sealed record Opened(Stream Stream, long Length, DateTimeOffset LastWriteUtc) : SourceOpen;

    /// <summary>Nothing there (the agent removed it) — a normal outcome, never a failure.</summary>
    public sealed record Gone : SourceOpen;

    /// <summary>Not copied, and why: a link on the way or at the name, not a regular file, another owner, more than one link.</summary>
    public sealed record Refused(string Why) : SourceOpen;
}

/// <summary>A destination folder opened level by level from the base (<see cref="IArchiveFiles.OpenFolderBeneath"/>).</summary>
public abstract record FolderBeneath
{
    private FolderBeneath()
    {
    }

    public sealed record Ready(BeneathFolder Folder) : FolderBeneath;

    public sealed record Refused(string Why) : FolderBeneath;
}

/// <summary>An exclusive create of an archived file (<see cref="IArchiveFiles.CreateExclusive"/>).</summary>
public abstract record ExclusiveFile
{
    private ExclusiveFile()
    {
    }

    /// <summary>A new, empty file (0600) open for writing; the caller writes, flushes and disposes it.</summary>
    public sealed record Created(FileStream Stream) : ExclusiveFile;

    /// <summary>Something already has that name; nothing was changed — never replaced.</summary>
    public sealed record Exists : ExclusiveFile;

    public sealed record Refused(string Why) : ExclusiveFile;
}

/// <summary>A file's SHA-256 as read from the disk, or why it could not be read.</summary>
public abstract record FileHash
{
    private FileHash()
    {
    }

    /// <summary>The lowercase hex SHA-256 of the bytes read, and how many there were.</summary>
    public sealed record Hashed(string Sha256, long Length) : FileHash;

    public sealed record Gone : FileHash;

    public sealed record Unreadable(string Why) : FileHash;
}

/// <summary>A rename that never replaces (<see cref="IArchiveFiles.QuarantineRename"/>, <see cref="IArchiveFiles.RenameBack"/>).</summary>
public abstract record NoReplaceRename
{
    private NoReplaceRename()
    {
    }

    public sealed record Renamed : NoReplaceRename;

    /// <summary>The new name exists — the file there (an agent's, re-created meanwhile) was kept, nothing renamed.</summary>
    public sealed record NameTaken : NoReplaceRename;

    /// <summary>The file to rename is not there.</summary>
    public sealed record Gone : NoReplaceRename;

    public sealed record Refused(string Why) : NoReplaceRename;
}

/// <summary>Whether a folder flush held (a new name in it survives a crash only when it did).</summary>
public abstract record FolderFlush
{
    private FolderFlush()
    {
    }

    public sealed record Done : FolderFlush;

    public sealed record Failed(string Why) : FolderFlush;
}

/// <summary>A verified removal (<see cref="IArchiveFiles.RemoveVerified"/>, <see cref="IArchiveFiles.RemoveEmptyFolder"/>).</summary>
public abstract record VerifiedRemoval
{
    private VerifiedRemoval()
    {
    }

    public sealed record Removed : VerifiedRemoval;

    /// <summary>Not removed, and why — its bytes differ from the archived copy, it is not a plain file, a folder is not empty.</summary>
    public sealed record Kept(string Why) : VerifiedRemoval;

    /// <summary>Nothing there — the agent removed it first; never a failure.</summary>
    public sealed record Gone : VerifiedRemoval;

    public sealed record Refused(string Why) : VerifiedRemoval;
}

/// <summary>
/// A destination folder held open for the archive's creates: on Linux its descriptor (every create and read-back goes through
/// it, so the folder cannot be swapped for a link after it was opened); on Windows its checked path.
/// </summary>
public sealed class BeneathFolder : IDisposable
{
    private readonly Action _close;

    internal BeneathFolder(string path, int descriptor, Action close)
    {
        Path = path;
        Descriptor = descriptor;
        _close = close;
    }

    /// <summary>The folder as this process spells it.</summary>
    public string Path { get; }

    /// <summary>The Linux descriptor (opened read-only, a directory, no link followed); -1 on Windows.</summary>
    internal int Descriptor { get; }

    public void Dispose() => _close();
}

/// <summary>
/// Plan §15r E9.S2a — the archive's ONLY way to touch a file (review M12: the seam): a streaming no-link source opener, a
/// destination tree created level by level from the base's descriptor, an exclusive no-follow create, a read-back hash, renames
/// that never replace, a removal that hashes before it unlinks (Windows: one handle whose delete disposition is set only after
/// equality), a non-recursive empty-folder removal. Every write and removal is judged by the deletion policy first: under an AI
/// agent's folder only a quarantine rename, a verified removal of a quarantined file whose archived copy is named, an empty-
/// folder removal and a restore's create are ever allowed — and never anything under <c>memory</c>.
/// </summary>
public interface IArchiveFiles
{
    /// <summary>Opens <paramref name="path"/> for reading from <paramref name="layoutRoot"/> through no link: a regular file of this
    /// account with one link (<c>O_NOFOLLOW | O_NONBLOCK</c> on Linux — a FIFO is never waited on).</summary>
    SourceOpen OpenSource(string layoutRoot, string path);

    /// <summary>Opens (creating each missing level, 0700) <paramref name="levels"/> below <paramref name="baseFolder"/>, each level from
    /// the previous level's descriptor — a level that is a link, or not a folder, refuses.</summary>
    FolderBeneath OpenFolderBeneath(string baseFolder, IReadOnlyList<string> levels, DeletionScope scope);

    /// <summary>Creates <paramref name="name"/> in <paramref name="folder"/> ONLY when nothing has that name (<c>O_CREAT | O_EXCL |
    /// O_NOFOLLOW</c>, 0600; Windows <c>CREATE_NEW</c> written through).</summary>
    ExclusiveFile CreateExclusive(BeneathFolder folder, string name, DeletionScope scope);

    /// <summary>Reads <paramref name="name"/> in <paramref name="folder"/> back and hashes it (Windows: unbuffered, from the disk).</summary>
    FileHash ReadBack(BeneathFolder folder, string name);

    /// <summary>Removes a file THIS run created in <paramref name="folder"/> (a copy that failed its verification).</summary>
    VerifiedRemoval RemoveOwnCopy(BeneathFolder folder, string name, DeletionScope scope);

    /// <summary>Flushes <paramref name="folder"/> itself (Linux: <c>fsync</c> of the directory, so a new name survives a crash).</summary>
    FolderFlush FlushFolder(BeneathFolder folder);

    /// <summary>Renames <paramref name="path"/> in its own folder to <paramref name="quarantinedName"/>, never replacing.</summary>
    NoReplaceRename QuarantineRename(string layoutRoot, string path, string quarantinedName, DeletionScope scope);

    /// <summary>Renames a quarantined file back to <paramref name="originalName"/>, never replacing what the agent wrote there since.</summary>
    NoReplaceRename RenameBack(string layoutRoot, string quarantinedPath, string originalName, DeletionScope scope);

    /// <summary>Removes the quarantined <paramref name="path"/> only when its bytes hash to <paramref name="expectedSha256"/> — the
    /// archived copy <paramref name="archivedCopy"/>'s hash — read from the same open file the removal acts on.</summary>
    VerifiedRemoval RemoveVerified(string layoutRoot, string path, string expectedSha256, string archivedCopy, DeletionScope scope);

    /// <summary>Removes <paramref name="folder"/> only when it is empty (never recursive).</summary>
    VerifiedRemoval RemoveEmptyFolder(string layoutRoot, string folder, DeletionScope scope);
}
