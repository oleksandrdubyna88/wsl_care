using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

using Microsoft.Win32.SafeHandles;

using WslCare.Core.Config;
using WslCare.Core.Files.Deletion;

namespace WslCare.Core.Files;

/// <summary>The primitive steps of the archive's seam the fault seam is asked between (plan §15r E9.S2a).</summary>
public enum ArchiveFileStep
{
    SourceOpened,
    FolderLevelReady,
    FolderLevelSynced,
    ExclusiveCreated,
    ReadBackChunk,
    OwnCopyRemoved,
    FolderFlushed,
    Renamed,
    RemovalOpened,
    RemovalHashChunk,
    RemovalHashed,
    Removed,
    FolderRemoved,
}

/// <summary>
/// Plan §15r E9.S2a — <see cref="IArchiveFiles"/> on the disk: every write and removal judged by the deletion policy on the REAL
/// paths first (the archive's permits: <see cref="DeletionPermit.ArchiveQuarantine"/>, <see cref="DeletionPermit.ArchiveRemoval"/>,
/// <see cref="DeletionPermit.RestoreIntoAgentFolder"/>), then acted on through the descriptors (Linux) or the checked handles
/// (Windows) of <see cref="BeneathWrites"/> — never through a link, never replacing, removing only what hashes equal.
/// </summary>
public sealed partial class PhysicalFileSystem
{
    private const string LinkOrNotAFolder = "a link (or not a folder) on its way, never followed";

    private static readonly char[] NameSeparators = OperatingSystem.IsWindows() ? ['/', '\\'] : ['/'];

    private static int HashBuffer => Math.Max(4096, Tuning.Current.Int(ConfigKeys.Archive.CopyBufferKib) * 1024 / 4096 * 4096);

    // ---- the verbs -------------------------------------------------------------------------------------------------------------

    public SourceOpen OpenSource(string layoutRoot, string path) => Below(layoutRoot, path) switch
    {
        { Problem.Length: > 0 } below => new SourceOpen.Refused(below.Problem),
        var below when OperatingSystem.IsLinux() => OpenSourceLinux(layoutRoot, below),
        var below when OperatingSystem.IsWindows() => OpenSourceWindows(layoutRoot, below),
        _ => new SourceOpen.Refused(NotThisOs),
    };

    public FolderBeneath OpenFolderBeneath(string baseFolder, IReadOnlyList<string> levels, DeletionScope scope)
    {
        var target = levels.Aggregate(baseFolder, Path.Combine);
        var problem = levels.Select(NameProblem).FirstOrDefault(p => p.Length > 0) ?? Why(JudgeArchive(FileOperation.Create, target, string.Empty, scope).Verdict);
        return problem.Length > 0 ? new FolderBeneath.Refused(problem)
            : OperatingSystem.IsLinux() ? FolderLinux(baseFolder, levels)
            : FolderWindows(baseFolder, levels);
    }

    public ExclusiveFile CreateExclusive(BeneathFolder folder, string name, DeletionScope scope)
    {
        var problem = NameProblem(name) is { Length: > 0 } bad ? bad : Why(JudgeArchive(FileOperation.Create, Path.Combine(folder.Path, name), string.Empty, scope).Verdict);
        return problem.Length > 0 ? new ExclusiveFile.Refused(problem)
            : OperatingSystem.IsLinux() ? CreateLinux(folder, name)
            : CreateWindows(folder, name);
    }

    public FileHash ReadBack(BeneathFolder folder, string name) =>
        NameProblem(name) is { Length: > 0 } bad ? new FileHash.Unreadable(bad)
        : OperatingSystem.IsLinux() ? ReadBackLinux(folder, name)
        : ReadBackWindows(folder, name);

    public VerifiedRemoval RemoveOwnCopy(BeneathFolder folder, string name, DeletionScope scope)
    {
        var problem = NameProblem(name) is { Length: > 0 } bad ? bad : Why(JudgeArchive(FileOperation.Delete, Path.Combine(folder.Path, name), string.Empty, scope).Verdict);
        return problem.Length > 0 ? new VerifiedRemoval.Refused(problem)
            : OperatingSystem.IsLinux() ? UnlinkOwnLinux(folder, name)
            : UnlinkOwnWindows(folder, name);
    }

    public FolderFlush FlushFolder(BeneathFolder folder)
    {
        var synced = OperatingSystem.IsLinux() && folder.Descriptor >= 0 ? BeneathWrites.Sync(folder.Descriptor) : new BeneathWrites.Native(0, 0);
        _onArchiveStep(ArchiveFileStep.FolderFlushed, folder.Path);
        return synced.Failed ? new FolderFlush.Failed($"{folder.Path} could not be flushed (errno {synced.Errno}); a name created in it may not survive a crash") : new FolderFlush.Done();
    }

    public NoReplaceRename QuarantineRename(string layoutRoot, string path, string quarantinedName, DeletionScope scope) =>
        Rename(layoutRoot, path, quarantinedName, scope);

    public NoReplaceRename RenameBack(string layoutRoot, string quarantinedPath, string originalName, DeletionScope scope) =>
        Rename(layoutRoot, quarantinedPath, originalName, scope);

    public VerifiedRemoval RemoveVerified(string layoutRoot, string path, string expectedSha256, string archivedCopy, DeletionScope scope)
    {
        var below = Below(layoutRoot, path);
        var problem = below.Problem.Length > 0 ? below.Problem : Why(JudgeArchive(FileOperation.Delete, path, string.Empty, scope, archivedCopy, folder: false).Verdict);
        return problem.Length > 0 ? new VerifiedRemoval.Refused(problem)
            : OperatingSystem.IsLinux() ? RemoveVerifiedLinux(layoutRoot, below, expectedSha256)
            : RemoveVerifiedWindows(layoutRoot, below, expectedSha256);
    }

    public VerifiedRemoval RemoveEmptyFolder(string layoutRoot, string folder, DeletionScope scope)
    {
        var below = Below(layoutRoot, folder);
        var problem = below.Problem.Length > 0 ? below.Problem : Why(JudgeArchive(FileOperation.Delete, folder, string.Empty, scope, string.Empty, folder: true).Verdict);
        return problem.Length > 0 ? new VerifiedRemoval.Refused(problem)
            : OperatingSystem.IsLinux() ? RemoveFolderLinux(layoutRoot, below)
            : RemoveFolderWindows(layoutRoot, below);
    }

    // ---- shared ----------------------------------------------------------------------------------------------------------------

    private const string NotThisOs = "the archive's seam runs on Linux and Windows only";

    /// <summary>A path below a trusted folder: the folders on the way, its own name — or why it is not one.</summary>
    private sealed record BelowRoot(IReadOnlyList<string> Folders, string Name, string Problem);

    private static BelowRoot Below(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var parts = relative.Split(NameSeparators);
        return Path.IsPathRooted(relative) || parts.Any(p => p is "" or "." or "..")
            ? new BelowRoot([], string.Empty, $"{path} is not a plain path below {root}")
            : new BelowRoot(parts[..^1], parts[^1], string.Empty);
    }

    /// <summary>Empty when <paramref name="name"/> is one plain name; why not otherwise.</summary>
    private static string NameProblem(string name) =>
        name is "" or "." or ".." || name.IndexOfAny(NameSeparators) >= 0 ? $"\"{name}\" is not one plain name" : string.Empty;

    private static string Why(DeletionVerdict verdict) => verdict is DeletionVerdict.Refused refused ? refused.Reason : string.Empty;

    /// <summary>The policy over the REAL paths, the archived copy and the folder flag included.</summary>
    private Judged JudgeArchive(FileOperation operation, string path, string destination, DeletionScope scope, string archivedCopy = "", bool folder = false)
    {
        var target = Real(path);
        var moveTo = destination.Length == 0 ? NoDestination : Real(destination);
        var copy = archivedCopy.Length == 0 ? NoDestination : Real(archivedCopy);
        var root = Real(scope.Root);
        if (FirstFailure(target, moveTo, copy, root) is { } failure)
        {
            return new Judged(DeletionPolicy.Unresolvable(operation, scope.Action, path, failure.Component, failure.Reason), string.Empty, string.Empty);
        }

        var request = new DeletionRequest(operation, PathOf(target), PathOf(moveTo), PathOf(root), scope.Action, scope.Permit) { ArchivedCopy = PathOf(copy), IsFolder = folder };
        return new Judged(_policy.Decide(request), request.RealPath, request.RealDestination);
    }

    private NoReplaceRename Rename(string layoutRoot, string path, string newName, DeletionScope scope)
    {
        var below = Below(layoutRoot, path);
        var problem = below.Problem.Length > 0 ? below.Problem
            : NameProblem(newName) is { Length: > 0 } bad ? bad
            : Why(JudgeArchive(FileOperation.Move, path, Path.Combine(Path.GetDirectoryName(path) ?? path, newName), scope).Verdict);
        return problem.Length > 0 ? new NoReplaceRename.Refused(problem)
            : OperatingSystem.IsLinux() ? RenameLinux(layoutRoot, below, newName)
            : RenameWindows(layoutRoot, below, newName);
    }

    /// <summary>The lowercase hex SHA-256 of a stream, the fault seam asked after every chunk.</summary>
    private (string Sha256, long Length) Hash(Stream stream, ArchiveFileStep chunk, string path)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[HashBuffer];
        long length = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            length += read;
            _onArchiveStep(chunk, path);
        }

        return (Convert.ToHexStringLower(sha.GetHashAndReset()), length);
    }

    // ---- Linux -----------------------------------------------------------------------------------------------------------------

    [SupportedOSPlatform("linux")]
    private SourceOpen OpenSourceLinux(string root, BelowRoot below)
    {
        var (folder, failed) = BeneathWrites.OpenChain(root, below.Folders, readable: false);
        if (folder.Failed)
        {
            return folder.Errno == BeneathWrites.NoEntry ? new SourceOpen.Gone() : new SourceOpen.Refused($"{LinkOrNotAFolder} (at {failed})");
        }

        var opened = BeneathWrites.OpenReadAt(folder.Value, below.Name);
        BeneathWrites.Close(folder.Value);
        return opened.Failed ? SourceFailure(opened.Errno, below.Name) : JudgedSource(opened.Value, below.Name);
    }

    private static SourceOpen SourceFailure(int errno, string name) => errno switch
    {
        BeneathWrites.NoEntry => new SourceOpen.Gone(),
        BeneathWrites.TooManyLinks => new SourceOpen.Refused($"{name} is a link, never followed"),
        _ => new SourceOpen.Refused($"{name} could not be opened (errno {errno})"),
    };

    /// <summary>A regular file of THIS account with ONE link — a FIFO, a device, another's file or a hard link to something else
    /// is never copied (the descriptor was opened non-blocking, so a FIFO is never waited on).</summary>
    [SupportedOSPlatform("linux")]
    private SourceOpen JudgedSource(int fd, string name)
    {
        var handle = new SafeFileHandle(fd, ownsHandle: true);
        var (ok, status) = BeneathWrites.Stat(fd, string.Empty);
        var problem = !ok ? $"{name}: its status could not be read"
            : !status.IsRegular ? $"{name} is not a regular file"
            : status.Links != 1 ? $"{name} has {status.Links} links (a hard link shares its bytes with another name)"
            : status.Owner != RegularFiles.EffectiveUid() ? $"{name} is owned by uid {status.Owner}, not this account"
            : string.Empty;
        if (problem.Length > 0)
        {
            handle.Dispose();
            return new SourceOpen.Refused(problem);
        }

        _onArchiveStep(ArchiveFileStep.SourceOpened, name);
        return new SourceOpen.Opened(new FileStream(handle, FileAccess.Read, bufferSize: 0), status.Size, status.LastWriteUtc);
    }

    [SupportedOSPlatform("linux")]
    private FolderBeneath FolderLinux(string baseFolder, IReadOnlyList<string> levels)
    {
        var current = BeneathWrites.OpenFolder(baseFolder, readable: true);
        if (current.Failed)
        {
            return new FolderBeneath.Refused($"{baseFolder}: {LinkOrNotAFolder} (errno {current.Errno})");
        }

        var path = baseFolder;
        foreach (var level in levels)
        {
            var next = LevelLinux(current.Value, level, Path.Combine(path, level));
            BeneathWrites.Close(current.Value);
            if (next.Failed)
            {
                return new FolderBeneath.Refused($"{LinkOrNotAFolder} (at {level}, errno {next.Errno})");
            }

            (current, path) = (next, Path.Combine(path, level));
            _onArchiveStep(ArchiveFileStep.FolderLevelReady, path);
        }

        var descriptor = current.Value;
        return new FolderBeneath.Ready(new BeneathFolder(path, descriptor, () => BeneathWrites.Close(descriptor)));
    }

    /// <summary>One level: created 0700 when missing (an existing one kept) and, when it is new, its ENTRY made durable by flushing
    /// the parent that holds it (risk consult 9/9.2); then opened from its parent's descriptor, never a link.</summary>
    [SupportedOSPlatform("linux")]
    private BeneathWrites.Native LevelLinux(int parent, string level, string path)
    {
        var made = BeneathWrites.MakeFolderAt(parent, level);
        return made switch
        {
            { Failed: true, Errno: not BeneathWrites.Exists } => made,
            { Failed: false } when SyncedParent(parent, path) is { Failed: true } failed => failed,
            _ => BeneathWrites.OpenFolderAt(parent, level, readable: true),
        };
    }

    [SupportedOSPlatform("linux")]
    private BeneathWrites.Native SyncedParent(int parent, string path)
    {
        var synced = BeneathWrites.Sync(parent);
        _onArchiveStep(ArchiveFileStep.FolderLevelSynced, path);
        return synced;
    }

    [SupportedOSPlatform("linux")]
    private ExclusiveFile CreateLinux(BeneathFolder folder, string name)
    {
        var created = BeneathWrites.CreateExclusiveAt(folder.Descriptor, name);
        if (created.Failed)
        {
            return created.Errno == BeneathWrites.Exists ? new ExclusiveFile.Exists() : new ExclusiveFile.Refused($"{name} could not be created (errno {created.Errno})");
        }

        _onArchiveStep(ArchiveFileStep.ExclusiveCreated, Path.Combine(folder.Path, name));
        return new ExclusiveFile.Created(new FileStream(new SafeFileHandle(created.Value, ownsHandle: true), FileAccess.Write, bufferSize: 0));
    }

    [SupportedOSPlatform("linux")]
    private FileHash ReadBackLinux(BeneathFolder folder, string name)
    {
        var opened = BeneathWrites.OpenReadAt(folder.Descriptor, name);
        if (opened.Failed)
        {
            return opened.Errno == BeneathWrites.NoEntry ? new FileHash.Gone() : new FileHash.Unreadable($"{name} could not be opened again (errno {opened.Errno})");
        }

        using var stream = new FileStream(new SafeFileHandle(opened.Value, ownsHandle: true), FileAccess.Read, bufferSize: 0);
        var (sha, length) = Hash(stream, ArchiveFileStep.ReadBackChunk, Path.Combine(folder.Path, name));
        return new FileHash.Hashed(sha, length);
    }

    [SupportedOSPlatform("linux")]
    private VerifiedRemoval UnlinkOwnLinux(BeneathFolder folder, string name)
    {
        var removed = BeneathWrites.UnlinkAt(folder.Descriptor, name, directory: false);
        _onArchiveStep(ArchiveFileStep.OwnCopyRemoved, Path.Combine(folder.Path, name));
        return !removed.Failed ? new VerifiedRemoval.Removed()
            : removed.Errno == BeneathWrites.NoEntry ? new VerifiedRemoval.Gone()
            : new VerifiedRemoval.Refused($"{name} could not be removed (errno {removed.Errno})");
    }

    [SupportedOSPlatform("linux")]
    private NoReplaceRename RenameLinux(string root, BelowRoot below, string newName)
    {
        var (folder, failed) = BeneathWrites.OpenChain(root, below.Folders, readable: false);
        if (folder.Failed)
        {
            return folder.Errno == BeneathWrites.NoEntry ? new NoReplaceRename.Gone() : new NoReplaceRename.Refused($"{LinkOrNotAFolder} (at {failed})");
        }

        var renamed = BeneathWrites.RenameNoReplace(folder.Value, below.Name, newName);
        BeneathWrites.Close(folder.Value);
        _onArchiveStep(ArchiveFileStep.Renamed, newName);
        return RenameAnswer(renamed);
    }

    private static NoReplaceRename RenameAnswer(BeneathWrites.Native renamed) => renamed switch
    {
        { Failed: false } => new NoReplaceRename.Renamed(),
        { Errno: BeneathWrites.Exists } => new NoReplaceRename.NameTaken(),
        { Errno: BeneathWrites.NoEntry } => new NoReplaceRename.Gone(),
        { Errno: BeneathWrites.InvalidArgument or BeneathWrites.NotSupported } => new NoReplaceRename.Refused("this file system cannot rename without replacing; nothing was renamed"),
        _ => new NoReplaceRename.Refused($"the rename failed (errno {renamed.Errno}); nothing was renamed"),
    };

    [SupportedOSPlatform("linux")]
    private VerifiedRemoval RemoveVerifiedLinux(string root, BelowRoot below, string expected)
    {
        var (folder, failed) = BeneathWrites.OpenChain(root, below.Folders, readable: false);
        if (folder.Failed)
        {
            return folder.Errno == BeneathWrites.NoEntry ? new VerifiedRemoval.Gone() : new VerifiedRemoval.Refused($"{LinkOrNotAFolder} (at {failed})");
        }

        try
        {
            return VerifyThenUnlink(folder.Value, below.Name, expected);
        }
        finally
        {
            BeneathWrites.Close(folder.Value);
        }
    }

    /// <summary>Linux: the file opened from its folder's descriptor, judged, hashed; only an equal hash — and the name still naming
    /// THAT file (its inode and device) — unlinks it. Residual: between that check and the unlink the name could be renamed over by
    /// another process of this account; the archive never does, and an agent writes by appending or creating, not by renaming
    /// onto a quarantine name.</summary>
    [SupportedOSPlatform("linux")]
    private VerifiedRemoval VerifyThenUnlink(int folder, string name, string expected)
    {
        var opened = BeneathWrites.OpenReadAt(folder, name);
        if (opened.Failed)
        {
            return opened.Errno == BeneathWrites.NoEntry ? new VerifiedRemoval.Gone() : new VerifiedRemoval.Kept($"{name} could not be opened (errno {opened.Errno})");
        }

        using var stream = new FileStream(new SafeFileHandle(opened.Value, ownsHandle: true), FileAccess.Read, bufferSize: 0);
        var (_, status) = BeneathWrites.Stat(opened.Value, string.Empty);
        _onArchiveStep(ArchiveFileStep.RemovalOpened, name);
        return !status.IsRegular || status.Links != 1 ? new VerifiedRemoval.Kept($"{name} is not a plain file of one link")
            : BeneathWrites.TakeWriteLease(opened.Value) is { Failed: true } lease ? new VerifiedRemoval.Kept($"{name} is open elsewhere (no write lease: errno {lease.Errno}) — a writer could still add to it; it stays")
            : HashThenUnlink(folder, name, expected, stream, status, opened.Value);
    }

    [SupportedOSPlatform("linux")]
    private VerifiedRemoval HashThenUnlink(int folder, string name, string expected, Stream stream, BeneathWrites.LinuxStatus opened, int descriptor)
    {
        var (sha, _) = Hash(stream, ArchiveFileStep.RemovalHashChunk, name);
        _onArchiveStep(ArchiveFileStep.RemovalHashed, name);
        var (still, now) = BeneathWrites.Stat(folder, name);
        var problem = !string.Equals(sha, expected, StringComparison.OrdinalIgnoreCase) ? $"{name}'s bytes differ from its archived copy; it stays"
            : !BeneathWrites.LeaseHeld(descriptor) ? $"{name} was opened while it was hashed; it stays"
            : !still || !now.SameFile(opened) ? $"{name} is no longer the file that was hashed; it stays"
            : string.Empty;
        return problem.Length > 0 ? new VerifiedRemoval.Kept(problem) : Unlinked(BeneathWrites.UnlinkAt(folder, name, directory: false), name);
    }

    private VerifiedRemoval Unlinked(BeneathWrites.Native removed, string name)
    {
        _onArchiveStep(ArchiveFileStep.Removed, name);
        return removed.Failed ? new VerifiedRemoval.Kept($"{name} could not be removed (errno {removed.Errno})") : new VerifiedRemoval.Removed();
    }

    [SupportedOSPlatform("linux")]
    private VerifiedRemoval RemoveFolderLinux(string root, BelowRoot below)
    {
        var (folder, failed) = BeneathWrites.OpenChain(root, below.Folders, readable: false);
        if (folder.Failed)
        {
            return folder.Errno == BeneathWrites.NoEntry ? new VerifiedRemoval.Gone() : new VerifiedRemoval.Refused($"{LinkOrNotAFolder} (at {failed})");
        }

        var removed = BeneathWrites.UnlinkAt(folder.Value, below.Name, directory: true);
        BeneathWrites.Close(folder.Value);
        _onArchiveStep(ArchiveFileStep.FolderRemoved, below.Name);
        return FolderAnswer(removed, below.Name);
    }

    private static VerifiedRemoval FolderAnswer(BeneathWrites.Native removed, string name) => removed switch
    {
        { Failed: false } => new VerifiedRemoval.Removed(),
        { Errno: BeneathWrites.NoEntry } => new VerifiedRemoval.Gone(),
        { Errno: BeneathWrites.NotEmpty or BeneathWrites.Exists } => new VerifiedRemoval.Kept($"{name} is not empty"),
        _ => new VerifiedRemoval.Kept($"{name} could not be removed as an empty folder (errno {removed.Errno})"),
    };

    // ---- Windows ---------------------------------------------------------------------------------------------------------------

    /// <summary>The first component below <paramref name="start"/> that is a reparse point (a symbolic link, a junction); empty when none.</summary>
    private static string ReparseOnTheWay(string start, IEnumerable<string> components)
    {
        var walked = start;
        foreach (var component in components)
        {
            walked = Path.Combine(walked, component);
            if (Path.Exists(walked) && File.GetAttributes(walked).HasFlag(FileAttributes.ReparsePoint))
            {
                return component;
            }
        }

        return string.Empty;
    }

    [SupportedOSPlatform("windows")]
    private SourceOpen OpenSourceWindows(string root, BelowRoot below)
    {
        var path = Path.Combine([root, .. below.Folders, below.Name]);
        if (ReparseOnTheWay(root, [.. below.Folders, below.Name]) is { Length: > 0 } link)
        {
            return new SourceOpen.Refused($"{LinkOrNotAFolder} (at {link})");
        }

        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            return JudgedSourceWindows(stream, below.Name);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new SourceOpen.Gone();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new SourceOpen.Refused($"{below.Name} could not be opened ({e.Message})");
        }
    }

    [SupportedOSPlatform("windows")]
    private SourceOpen JudgedSourceWindows(FileStream stream, string name)
    {
        var (ok, attributes, links) = BeneathWrites.Describe(stream.SafeFileHandle);
        var problem = !ok ? $"{name}: its information could not be read"
            : (attributes & (BeneathWrites.WindowsReparsePoint | BeneathWrites.WindowsDirectory)) != 0 ? $"{name} is not a regular file"
            : links != 1 ? $"{name} has {links} links (a hard link shares its bytes with another name)"
            : string.Empty;
        if (problem.Length > 0)
        {
            stream.Dispose();
            return new SourceOpen.Refused(problem);
        }

        _onArchiveStep(ArchiveFileStep.SourceOpened, name);
        return new SourceOpen.Opened(stream, stream.Length, File.GetLastWriteTimeUtc(stream.SafeFileHandle));
    }

    private FolderBeneath FolderWindows(string baseFolder, IReadOnlyList<string> levels)
    {
        if (ReparseOnTheWay(Path.GetDirectoryName(baseFolder) ?? baseFolder, [Path.GetFileName(baseFolder)]) is { Length: > 0 } baseLink)
        {
            return new FolderBeneath.Refused($"{LinkOrNotAFolder} (at {baseLink})");
        }

        var path = baseFolder;
        foreach (var level in levels)
        {
            path = Path.Combine(path, level);
            if (LevelWindows(path) is { Length: > 0 } problem)
            {
                return new FolderBeneath.Refused($"{problem} (at {level})");
            }

            _onArchiveStep(ArchiveFileStep.FolderLevelReady, path);
        }

        return new FolderBeneath.Ready(new BeneathFolder(path, -1, static () => { }));
    }

    /// <summary>One level on Windows: an existing one must be a folder and no reparse point; a missing one is created and checked.
    /// Residual: a reparse point made between the check and the next level's create is not seen (the Windows side writes into its
    /// own user's base; Linux's descriptor chain has no such window).</summary>
    private static string LevelWindows(string path)
    {
        if (!Path.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        var attributes = File.GetAttributes(path);
        return attributes.HasFlag(FileAttributes.ReparsePoint) || !attributes.HasFlag(FileAttributes.Directory) ? LinkOrNotAFolder : string.Empty;
    }

    private ExclusiveFile CreateWindows(BeneathFolder folder, string name)
    {
        var path = Path.Combine(folder.Path, name);
        try
        {
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough);
            _onArchiveStep(ArchiveFileStep.ExclusiveCreated, path);
            return new ExclusiveFile.Created(stream);
        }
        catch (IOException) when (Path.Exists(path))
        {
            return new ExclusiveFile.Exists();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new ExclusiveFile.Refused($"{name} could not be created ({e.Message})");
        }
    }

    /// <summary>Windows: read back past the system cache (<c>FILE_FLAG_NO_BUFFERING</c>) into a sector-aligned buffer.</summary>
    private FileHash ReadBackWindows(BeneathFolder folder, string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new FileHash.Unreadable(NotThisOs);
        }

        var path = Path.Combine(folder.Path, name);
        var (handle, error) = BeneathWrites.OpenWindows(path, delete: false, unbuffered: true);
        using (handle)
        {
            return error switch
            {
                0 => HashedUnbuffered(handle, path),
                BeneathWrites.WindowsNotFound or BeneathWrites.WindowsPathNotFound => new FileHash.Gone(),
                _ => new FileHash.Unreadable($"{name} could not be opened again (error {error})"),
            };
        }
    }

    private FileHash HashedUnbuffered(SafeFileHandle handle, string path)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var size = HashBuffer;
        var pinned = GC.AllocateArray<byte>(size + 4096, pinned: true);
        var skew = (int)(Marshal.UnsafeAddrOfPinnedArrayElement(pinned, 0).ToInt64() % 4096);
        var aligned = pinned.AsMemory(skew == 0 ? 0 : 4096 - skew, size);
        long offset = 0;
        int read;
        while ((read = RandomAccess.Read(handle, aligned.Span, offset)) > 0)
        {
            sha.AppendData(aligned.Span[..read]);
            offset += read;
            _onArchiveStep(ArchiveFileStep.ReadBackChunk, path);
        }

        return new FileHash.Hashed(Convert.ToHexStringLower(sha.GetHashAndReset()), offset);
    }

    private VerifiedRemoval UnlinkOwnWindows(BeneathFolder folder, string name)
    {
        var path = Path.Combine(folder.Path, name);
        if (!File.Exists(path))
        {
            return new VerifiedRemoval.Gone();
        }

        File.Delete(path);
        _onArchiveStep(ArchiveFileStep.OwnCopyRemoved, path);
        return new VerifiedRemoval.Removed();
    }

    private NoReplaceRename RenameWindows(string root, BelowRoot below, string newName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new NoReplaceRename.Refused(NotThisOs);
        }

        if (ReparseOnTheWay(root, [.. below.Folders, below.Name]) is { Length: > 0 } link)
        {
            return new NoReplaceRename.Refused($"{LinkOrNotAFolder} (at {link})");
        }

        var folder = Path.Combine([root, .. below.Folders]);
        var error = BeneathWrites.MoveNoReplace(Path.Combine(folder, below.Name), Path.Combine(folder, newName));
        _onArchiveStep(ArchiveFileStep.Renamed, newName);
        return error switch
        {
            0 => new NoReplaceRename.Renamed(),
            BeneathWrites.WindowsFileExists or BeneathWrites.WindowsAlreadyExists => new NoReplaceRename.NameTaken(),
            BeneathWrites.WindowsNotFound or BeneathWrites.WindowsPathNotFound => new NoReplaceRename.Gone(),
            _ => new NoReplaceRename.Refused($"the rename failed (error {error}); nothing was renamed"),
        };
    }

    /// <summary>Windows (review B2): ONE handle, <c>DELETE | READ</c>, sharing READ only — nobody writes, renames or deletes it while
    /// it is held; the bytes hashed through it; the delete disposition set ONLY after they are equal. A stop anywhere before —
    /// an exception, a kill — closes the handle with no disposition, and the file stays. Never <c>DeleteOnClose</c>.</summary>
    private VerifiedRemoval RemoveVerifiedWindows(string root, BelowRoot below, string expected) =>
        !OperatingSystem.IsWindows() ? new VerifiedRemoval.Refused(NotThisOs)
        : ReparseOnTheWay(root, below.Folders) is { Length: > 0 } link ? new VerifiedRemoval.Refused($"{LinkOrNotAFolder} (at {link})")
        : OpenedForRemoval(Path.Combine([root, .. below.Folders, below.Name]), below.Name, expected);

    [SupportedOSPlatform("windows")]
    private VerifiedRemoval OpenedForRemoval(string path, string name, string expected)
    {
        var (handle, error) = BeneathWrites.OpenWindows(path, delete: true, unbuffered: false);
        using (handle)
        {
            return error switch
            {
                0 => VerifyThenDispose(handle, name, expected),
                BeneathWrites.WindowsNotFound or BeneathWrites.WindowsPathNotFound => new VerifiedRemoval.Gone(),
                BeneathWrites.WindowsSharing => new VerifiedRemoval.Kept($"{name} is open in another process; it stays"),
                _ => new VerifiedRemoval.Kept($"{name} could not be opened (error {error}); it stays"),
            };
        }
    }

    [SupportedOSPlatform("windows")]
    private VerifiedRemoval VerifyThenDispose(SafeFileHandle handle, string name, string expected)
    {
        _onArchiveStep(ArchiveFileStep.RemovalOpened, name);
        if (!IsPlainWindowsFile(handle))
        {
            return new VerifiedRemoval.Kept($"{name} is not a plain file of one link");
        }

        using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 0);
        var (sha, _) = Hash(stream, ArchiveFileStep.RemovalHashChunk, name);
        _onArchiveStep(ArchiveFileStep.RemovalHashed, name);
        return string.Equals(sha, expected, StringComparison.OrdinalIgnoreCase) ? Disposed(BeneathWrites.MarkForDeletion(handle), name) : new VerifiedRemoval.Kept($"{name}'s bytes differ from its archived copy; it stays");
    }

    /// <summary>A regular file — no folder, no reparse point — of ONE link.</summary>
    [SupportedOSPlatform("windows")]
    private static bool IsPlainWindowsFile(SafeFileHandle handle) =>
        BeneathWrites.Describe(handle) is (true, var attributes, 1) && (attributes & (BeneathWrites.WindowsReparsePoint | BeneathWrites.WindowsDirectory)) == 0;

    private VerifiedRemoval Disposed(int error, string name)
    {
        _onArchiveStep(ArchiveFileStep.Removed, name);
        return error == 0 ? new VerifiedRemoval.Removed() : new VerifiedRemoval.Kept($"{name} could not be marked for deletion (error {error}); it stays");
    }

    private VerifiedRemoval RemoveFolderWindows(string root, BelowRoot below)
    {
        var path = Path.Combine([root, .. below.Folders, below.Name]);
        if (ReparseOnTheWay(root, [.. below.Folders, below.Name]) is { Length: > 0 } link)
        {
            return new VerifiedRemoval.Refused($"{LinkOrNotAFolder} (at {link})");
        }

        try
        {
            Directory.Delete(path, recursive: false);
            _onArchiveStep(ArchiveFileStep.FolderRemoved, path);
            return new VerifiedRemoval.Removed();
        }
        catch (DirectoryNotFoundException)
        {
            return new VerifiedRemoval.Gone();
        }
        catch (IOException)
        {
            return new VerifiedRemoval.Kept($"{below.Name} is not empty");
        }
    }
}
