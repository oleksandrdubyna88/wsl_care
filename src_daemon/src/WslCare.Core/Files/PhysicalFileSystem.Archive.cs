using System.Runtime.Versioning;
using System.Security.Cryptography;

using Microsoft.Win32.SafeHandles;

using WslCare.Core.Config;
using WslCare.Core.Files.Deletion;

namespace WslCare.Core.Files;

/// <summary>The primitive steps of the archive's seam the fault seam is asked between (plan §15r E9.S2a).</summary>
public enum ArchiveFileStep
{
    /// <summary>The path was judged by the policy and located on its real path; nothing is opened along it yet.</summary>
    PathChecked,
    SourceOpened,
    FolderLevelReady,
    FolderLevelSynced,
    ExclusiveCreated,
    ReadBackChunk,
    OwnCopyRemoved,
    FolderFlushed,
    Appended,
    Renamed,
    ArchivedCopyHashed,
    RemovalOpened,
    RemovalHashChunk,
    RemovalHashed,
    Removed,
    FolderRemoved,
}

/// <summary>
/// Plan §15r E9.S2a — <see cref="IArchiveFiles"/> on the disk: every write and removal judged by the deletion policy on the REAL
/// paths first (the archive's permits: <see cref="DeletionPermit.ArchiveQuarantine"/>, <see cref="DeletionPermit.ArchiveRemoval"/>,
/// <see cref="DeletionPermit.RestoreIntoAgentFolder"/>), then acted on along those real paths — Linux through a descriptor chain
/// from the file system's root with no link followed at any level, Windows through handles that are asked where they really are
/// (own review round, security m1) — never replacing, removing only what hashes equal.
/// </summary>
public sealed partial class PhysicalFileSystem
{
    private const string LinkOrNotAFolder = "a link (or not a folder) on its way, never followed";

    private static readonly char[] NameSeparators = OperatingSystem.IsWindows() ? ['/', '\\'] : ['/'];

    private static int HashBuffer => Math.Max(4096, Tuning.Current.Int(ConfigKeys.Archive.CopyBufferKib) * 1024 / 4096 * 4096);

    // ---- the verbs -------------------------------------------------------------------------------------------------------------

    public SourceOpen OpenSource(string layoutRoot, string path) => Locate(layoutRoot, path) switch
    {
        { Problem.Length: > 0 } located => new SourceOpen.Refused(located.Problem),
        var located when OperatingSystem.IsLinux() => OpenSourceLinux(located),
        var located when OperatingSystem.IsWindows() => OpenSourceWindows(located),
        _ => new SourceOpen.Refused(NotThisOs),
    };

    public FolderBeneath OpenFolderBeneath(string baseFolder, IReadOnlyList<string> levels, DeletionScope scope) =>
        FolderProblem(baseFolder, levels, scope) is { Length: > 0 } problem ? new FolderBeneath.Refused(problem) : FolderByOs(baseFolder, levels, create: true);

    public FolderBeneath OpenExistingFolderBeneath(string baseFolder, IReadOnlyList<string> levels) =>
        LevelsProblem(levels) is { Length: > 0 } bad ? new FolderBeneath.Refused(bad) : FolderByOs(baseFolder, levels, create: false);

    public ExclusiveFile CreateExclusive(BeneathFolder folder, string name, DeletionScope scope) =>
        folder is OpenedFolder own ? own.With(() => CreateIn(own, name, scope), why => new ExclusiveFile.Refused(why)) : new ExclusiveFile.Refused(ForeignFolder(folder));

    public FileHash ReadBack(BeneathFolder folder, string name) =>
        folder is OpenedFolder own ? own.With(() => ReadBackIn(own, name), why => new FileHash.Unreadable(why)) : new FileHash.Unreadable(ForeignFolder(folder));

    public VerifiedRemoval RemoveOwnCopy(BeneathFolder folder, string name, FileIdentity created, DeletionScope scope) =>
        folder is OpenedFolder own ? own.With(() => RemoveOwnIn(own, name, created, scope), why => new VerifiedRemoval.Refused(why)) : new VerifiedRemoval.Refused(ForeignFolder(folder));

    public FolderFlush FlushFolder(BeneathFolder folder) =>
        folder is OpenedFolder own ? own.With(() => Flushed(own), why => new FolderFlush.Failed(why)) : new FolderFlush.Failed(ForeignFolder(folder));

    public NoReplaceRename QuarantineRename(string layoutRoot, string path, string quarantinedName, DeletionScope scope) =>
        Rename(layoutRoot, path, quarantinedName, scope);

    public NoReplaceRename RenameBack(string layoutRoot, string quarantinedPath, string originalName, DeletionScope scope) =>
        Rename(layoutRoot, quarantinedPath, originalName, scope);

    public NoReplaceRename PromoteRestored(string layoutRoot, string temporaryPath, string finalName, DeletionScope scope) =>
        Rename(layoutRoot, temporaryPath, finalName, scope);

    public VerifiedRemoval RemoveVerified(string layoutRoot, string path, string expectedSha256, string archivedCopy, DeletionScope scope)
    {
        var located = Locate(layoutRoot, path);
        var problem = located.Problem.Length > 0 ? located.Problem : Why(JudgeArchive(FileOperation.Delete, path, string.Empty, scope, archivedCopy, folder: false).Verdict);
        return problem.Length > 0 ? new VerifiedRemoval.Refused(problem) : CopyThenSource(located, archivedCopy, expectedSha256);
    }

    public VerifiedRemoval RemoveEmptyFolder(string layoutRoot, string folder, DeletionScope scope)
    {
        var located = Locate(layoutRoot, folder);
        var problem = located.Problem.Length > 0 ? located.Problem : Why(JudgeArchive(FileOperation.Delete, folder, string.Empty, scope, string.Empty, folder: true).Verdict);
        return problem.Length > 0 ? new VerifiedRemoval.Refused(problem)
            : OperatingSystem.IsLinux() ? RemoveFolderLinux(located)
            : RemoveFolderWindows(located);
    }

    // ---- shared ----------------------------------------------------------------------------------------------------------------

    private const string NotThisOs = "the archive's seam runs on Linux and Windows only";

    /// <summary>The folder this seam opened (gate round finding 2): its handle — the Linux descriptor, or the Windows folder handle
    /// that pins it. Every verb acts INSIDE <see cref="With{T}"/>, which holds a reference on the handle for the call: a disposed
    /// folder is refused, and its descriptor number is never reused under a verb (own review round, security M1).</summary>
    internal sealed class OpenedFolder(string path, SafeFileHandle handle) : BeneathFolder(path)
    {
        internal SafeFileHandle Handle { get; } = handle;

        /// <summary>The Linux descriptor — valid only inside <see cref="With{T}"/>.</summary>
        internal int Descriptor => BeneathWrites.Descriptor(Handle);

        internal T With<T>(Func<T> act, Func<string, T> refused)
        {
            var added = false;
            try
            {
                Handle.DangerousAddRef(ref added);
                return act();
            }
            catch (ObjectDisposedException)
            {
                return refused($"{Path}: this folder was closed; nothing was done in it");
            }
            finally
            {
                if (added)
                {
                    Handle.DangerousRelease();
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Handle.Dispose();
            }
        }
    }

    private static string ForeignFolder(BeneathFolder folder) => $"{folder.Path}: a folder this seam did not open is never trusted";

    private string FolderProblem(string baseFolder, IReadOnlyList<string> levels, DeletionScope scope) =>
        LevelsProblem(levels) is { Length: > 0 } bad ? bad : Why(JudgeArchive(FileOperation.Create, levels.Aggregate(baseFolder, Path.Combine), string.Empty, scope).Verdict);

    private static string LevelsProblem(IReadOnlyList<string> levels) => levels.Select(NameProblem).FirstOrDefault(p => p.Length > 0) ?? string.Empty;

    private FolderBeneath FolderByOs(string baseFolder, IReadOnlyList<string> levels, bool create) =>
        OperatingSystem.IsLinux() ? FolderLinux(baseFolder, levels, create)
        : OperatingSystem.IsWindows() ? FolderWindows(baseFolder, levels, create)
        : new FolderBeneath.Refused(NotThisOs);

    private ExclusiveFile CreateIn(OpenedFolder folder, string name, DeletionScope scope)
    {
        var problem = NameProblem(name) is { Length: > 0 } bad ? bad : Why(JudgeArchive(FileOperation.Create, Path.Combine(folder.Path, name), string.Empty, scope).Verdict);
        return problem.Length > 0 ? new ExclusiveFile.Refused(problem)
            : OperatingSystem.IsLinux() ? CreateLinux(folder, name)
            : CreateWindows(folder, name);
    }

    private FileHash ReadBackIn(OpenedFolder folder, string name) =>
        NameProblem(name) is { Length: > 0 } bad ? new FileHash.Unreadable(bad)
        : OperatingSystem.IsLinux() ? ReadBackLinux(folder, name)
        : ReadBackWindows(folder, name);

    private VerifiedRemoval RemoveOwnIn(OpenedFolder folder, string name, FileIdentity created, DeletionScope scope)
    {
        var problem = NameProblem(name) is { Length: > 0 } bad ? bad : Why(JudgeArchive(FileOperation.Delete, Path.Combine(folder.Path, name), string.Empty, scope).Verdict);
        return problem.Length > 0 ? new VerifiedRemoval.Refused(problem)
            : OperatingSystem.IsLinux() ? UnlinkOwnLinux(folder, name, created)
            : UnlinkOwnWindows(folder, name, created);
    }

    /// <summary>The folder itself flushed — Linux <c>fsync</c> of its descriptor, Windows <c>FlushFileBuffers</c> of its held handle
    /// (gate round finding 4: the first build answered Done on Windows without flushing anything).</summary>
    private FolderFlush Flushed(OpenedFolder folder)
    {
        var error = FlushError(folder);
        _onArchiveStep(ArchiveFileStep.FolderFlushed, folder.Path);
        return error != 0 ? new FolderFlush.Failed($"{folder.Path} could not be flushed (error {error}); a name created in it may not survive a crash") : new FolderFlush.Done();
    }

    private static int FlushError(OpenedFolder folder) =>
        OperatingSystem.IsLinux() ? BeneathWrites.Sync(folder.Descriptor) is { Failed: true } failed ? failed.Errno : 0
        : OperatingSystem.IsWindows() ? BeneathWrites.FlushWindows(folder.Handle)
        : -1;

    /// <summary>A target on its REAL, link-free path (own review round, security m1): the file system's root it hangs from, every
    /// folder from there, its own name — or why it is not one. The acts open along THIS path, never the spelled one, so a link
    /// swapped in anywhere after the policy judged it — the root's ancestors included — refuses the act.</summary>
    private sealed record Located(string RealPath, string Top, IReadOnlyList<string> Folders, string Name, string Problem)
    {
        public static Located Refused(string why) => new(string.Empty, string.Empty, [], string.Empty, why);
    }

    /// <summary><paramref name="path"/> located on its real path, which must lie below <paramref name="root"/>'s real path — each part
    /// below it one plain name (on Windows also one NTFS can hold: own review round, security m3).</summary>
    private Located Locate(string root, string path)
    {
        var realRoot = Real(root);
        var realPath = Real(path);
        if (FirstFailure(realRoot, realPath) is { } failure)
        {
            return Located.Refused($"{path}: {failure.Reason}");
        }

        var below = Below(PathOf(realRoot), PathOf(realPath));
        return below.Problem.Length > 0 ? Located.Refused($"{path}: {below.Problem}") : FromFileSystemRoot(PathOf(realPath));
    }

    /// <summary>A real path split from the file system's root (<c>/</c>, <c>C:\</c>, <c>\\server\share\</c>).</summary>
    private static Located FromFileSystemRoot(string realPath)
    {
        var top = Path.GetPathRoot(realPath) ?? string.Empty;
        var parts = realPath[top.Length..].Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries);
        return top.Length == 0 || parts.Length == 0
            ? Located.Refused($"{realPath} is not a file below a root")
            : new Located(realPath, top, parts[..^1], parts[^1], string.Empty);
    }

    /// <summary>A path below a trusted folder: the folders on the way, its own name — or why it is not one.</summary>
    private sealed record BelowRoot(IReadOnlyList<string> Folders, string Name, string Problem);

    private static BelowRoot Below(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var parts = relative.Split(NameSeparators);
        return Path.IsPathRooted(relative) || parts.Any(p => NameProblem(p).Length > 0)
            ? new BelowRoot([], string.Empty, $"not a plain path below {root}")
            : new BelowRoot(parts[..^1], parts[^1], string.Empty);
    }

    /// <summary>Empty when <paramref name="name"/> is one plain name; why not otherwise. On Windows also a name NTFS holds as itself
    /// — no <c>:</c> (an alternate stream of another file), no reserved device name, no trailing dot or space (security m3).</summary>
    private static string NameProblem(string name) =>
        name is "" or "." or ".." || name.IndexOfAny(NameSeparators) >= 0 ? $"\"{name}\" is not one plain name"
        : OperatingSystem.IsWindows() ? Archive.ArchiveNames.Problem(name)
        : string.Empty;

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
        var located = Locate(layoutRoot, path);
        var problem = RenameProblem(located, path, newName, scope);
        return problem.Length > 0 ? new NoReplaceRename.Refused(problem)
            : OperatingSystem.IsLinux() ? RenameLinux(located, newName)
            : RenameWindows(located, newName);
    }

    private string RenameProblem(Located located, string path, string newName, DeletionScope scope) =>
        located.Problem.Length > 0 ? located.Problem
        : NameProblem(newName) is { Length: > 0 } bad ? bad
        : Why(JudgeArchive(FileOperation.Move, path, Path.Combine(Path.GetDirectoryName(path) ?? path, newName), scope).Verdict);

    /// <summary>Own review round, security M2: the archived copy is opened through no link and hashed (Windows past the cache) BEFORE
    /// the source is touched — a copy that is missing, unreadable or changed keeps the source; then the source's own checks.</summary>
    private VerifiedRemoval CopyThenSource(Located source, string archivedCopy, string expected) =>
        ArchivedCopyProblem(HashArchived(archivedCopy), expected) is { Length: > 0 } problem ? new VerifiedRemoval.Kept(problem)
        : OperatingSystem.IsLinux() ? RemoveVerifiedLinux(source, expected)
        : RemoveVerifiedWindows(source, expected);

    private FileHash HashArchived(string archivedCopy)
    {
        var located = Real(archivedCopy) is RealPathResult.Resolved resolved ? FromFileSystemRoot(resolved.Path) : Located.Refused("its path could not be resolved");
        var hashed = located.Problem.Length > 0 ? new FileHash.Unreadable(located.Problem)
            : OperatingSystem.IsLinux() ? HashFileLinux(located)
            : HashFileWindows(located);
        _onArchiveStep(ArchiveFileStep.ArchivedCopyHashed, archivedCopy);
        return hashed;
    }

    private static string ArchivedCopyProblem(FileHash copy, string expected) => copy switch
    {
        FileHash.Hashed hashed => string.Equals(hashed.Sha256, expected, StringComparison.OrdinalIgnoreCase) ? string.Empty : "its archived copy's bytes differ from the expected hash; the source stays",
        FileHash.Gone => "its archived copy is missing; the source stays",
        _ => "its archived copy could not be read; the source stays",
    };

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

    /// <summary>Linux: the folder holding <paramref name="at"/>, opened from the file system's root along its real path with
    /// <c>O_NOFOLLOW</c> at every level, handed to <paramref name="act"/> and closed after; <paramref name="failed"/> answers a level
    /// that is missing (<c>ENOENT</c>) or a link.</summary>
    /// <summary>The fault seam asked that the path was judged and located — before anything is opened along it.</summary>
    private Located Checked(Located at)
    {
        _onArchiveStep(ArchiveFileStep.PathChecked, at.Name);
        return at;
    }

    [SupportedOSPlatform("linux")]
    private static T WithChain<T>(Located at, bool readable, Func<int, T> act, Func<int, string, T> failed)
    {
        var chain = BeneathWrites.OpenChain(at.Top, at.Folders, readable);
        if (chain.Folder.Failed)
        {
            return failed(chain.Folder.Errno, chain.FailedAt);
        }

        try
        {
            return act(chain.Folder.Value);
        }
        finally
        {
            BeneathWrites.Close(chain.Folder.Value);
        }
    }

    [SupportedOSPlatform("linux")]
    private SourceOpen OpenSourceLinux(Located at) =>
        WithChain(Checked(at), readable: false, folder =>
        {
            return BeneathWrites.OpenReadAt(folder, at.Name) switch
            {
                NativeOpen.Opened opened => JudgedSource(opened.Handle, at.Name),
                NativeOpen.Failed failed => SourceFailure(failed.Error, at.Name),
                _ => new SourceOpen.Refused(at.Name),
            };
        }, (errno, level) => errno == BeneathWrites.NoEntry ? new SourceOpen.Gone() : new SourceOpen.Refused($"{LinkOrNotAFolder} (at {level})"));

    private static SourceOpen SourceFailure(int errno, string name) => errno switch
    {
        BeneathWrites.NoEntry => new SourceOpen.Gone(),
        BeneathWrites.TooManyLinks => new SourceOpen.Refused($"{name} is a link, never followed"),
        _ => new SourceOpen.Refused($"{name} could not be opened (errno {errno})"),
    };

    /// <summary>A regular file of THIS account with ONE link — a FIFO, a device, another's file or a hard link to something else
    /// is never copied (the descriptor was opened non-blocking, so a FIFO is never waited on).</summary>
    [SupportedOSPlatform("linux")]
    private SourceOpen JudgedSource(SafeFileHandle handle, string name)
    {
        var status = BeneathWrites.Stat(BeneathWrites.Descriptor(handle), string.Empty);
        var problem = ArchiveSourceRules.LinuxProblem(name, status, RegularFiles.EffectiveUid());
        if (problem.Length > 0)
        {
            handle.Dispose();
            return new SourceOpen.Refused(problem);
        }

        _onArchiveStep(ArchiveFileStep.SourceOpened, name);
        return new SourceOpen.Opened(new FileStream(handle, FileAccess.Read, bufferSize: 0), status.Size, status.LastWriteUtc);
    }

    /// <summary>Linux: the base opened from the file system's root along its REAL path, no link at any level; then each level made
    /// (0700) or opened from its parent's descriptor.</summary>
    [SupportedOSPlatform("linux")]
    private FolderBeneath FolderLinux(string baseFolder, IReadOnlyList<string> levels, bool create)
    {
        var at = Real(baseFolder) is RealPathResult.Resolved resolved ? FromFileSystemRoot(resolved.Path) : Located.Refused($"{baseFolder} could not be resolved");
        if (at.Problem.Length > 0)
        {
            return new FolderBeneath.Refused(at.Problem);
        }

        var chain = BeneathWrites.OpenChain(at.Top, [.. at.Folders, at.Name], readable: true);
        return chain.Folder.Failed
            ? FolderFailure(chain.Folder.Errno, create, $"{baseFolder}: {LinkOrNotAFolder} (at {chain.FailedAt}, errno {chain.Folder.Errno})")
            : LevelsLinux(chain.Folder.Value, at.RealPath, levels, create);
    }

    [SupportedOSPlatform("linux")]
    private FolderBeneath LevelsLinux(int current, string path, IReadOnlyList<string> levels, bool create)
    {
        foreach (var level in levels)
        {
            var next = create ? LevelLinux(current, level, Path.Combine(path, level)) : BeneathWrites.OpenFolderAt(current, level, readable: true);
            BeneathWrites.Close(current);
            if (next.Failed)
            {
                return FolderFailure(next.Errno, create, $"{LinkOrNotAFolder} (at {level}, errno {next.Errno})");
            }

            (current, path) = (next.Value, Path.Combine(path, level));
            _onArchiveStep(ArchiveFileStep.FolderLevelReady, path);
        }

        return new FolderBeneath.Ready(new OpenedFolder(path, new SafeFileHandle(current, ownsHandle: true)));
    }

    /// <summary>A level that could not be opened: MISSING when the reader asked for existing levels only (E9.S2b) and it is not there.</summary>
    private static FolderBeneath FolderFailure(int errno, bool create, string why) =>
        !create && errno == BeneathWrites.NoEntry ? new FolderBeneath.Missing() : new FolderBeneath.Refused(why);

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
    private ExclusiveFile CreateLinux(OpenedFolder folder, string name) => BeneathWrites.CreateExclusiveAt(folder.Descriptor, name) switch
    {
        NativeOpen.Opened opened => CreatedLinux(opened.Handle, Path.Combine(folder.Path, name)),
        NativeOpen.Failed { Error: BeneathWrites.Exists } => new ExclusiveFile.Exists(),
        NativeOpen.Failed failed => new ExclusiveFile.Refused($"{name} could not be created (errno {failed.Error})"),
        _ => new ExclusiveFile.Refused(name),
    };

    [SupportedOSPlatform("linux")]
    private ExclusiveFile CreatedLinux(SafeFileHandle handle, string path)
    {
        var identity = BeneathWrites.Stat(BeneathWrites.Descriptor(handle), string.Empty).Identity;
        _onArchiveStep(ArchiveFileStep.ExclusiveCreated, path);
        return new ExclusiveFile.Created(new FileStream(handle, FileAccess.Write, bufferSize: 0), identity);
    }

    [SupportedOSPlatform("linux")]
    private FileHash ReadBackLinux(OpenedFolder folder, string name) => BeneathWrites.OpenReadAt(folder.Descriptor, name) switch
    {
        NativeOpen.Opened opened => HashedThrough(opened.Handle, Path.Combine(folder.Path, name)),
        NativeOpen.Failed { Error: BeneathWrites.NoEntry } => new FileHash.Gone(),
        NativeOpen.Failed failed => new FileHash.Unreadable($"{name} could not be opened again (errno {failed.Error})"),
        _ => new FileHash.Unreadable(name),
    };

    /// <summary>A plain file's bytes hashed through its open handle, then the handle closed; anything else unreadable.</summary>
    [SupportedOSPlatform("linux")]
    private FileHash HashedThrough(SafeFileHandle handle, string path)
    {
        using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 0);
        if (!BeneathWrites.Stat(BeneathWrites.Descriptor(handle), string.Empty).IsPlainFile)
        {
            return new FileHash.Unreadable($"{path} is not a plain file of one link");
        }

        var (sha, length) = Hash(stream, ArchiveFileStep.ReadBackChunk, path);
        return new FileHash.Hashed(sha, length);
    }

    /// <summary>Linux (own review round M2): the archive's own copy is unlinked only while the name still names the file this run
    /// created — its device and inode. Residual: a rename onto the name between the check and the unlink (inside the base, which
    /// the side's lease gives to one writer).</summary>
    [SupportedOSPlatform("linux")]
    private VerifiedRemoval UnlinkOwnLinux(OpenedFolder folder, string name, FileIdentity created)
    {
        var now = BeneathWrites.Stat(folder.Descriptor, name);
        if (!now.Known || !now.IsRegular || now.Identity != created)
        {
            return now.Known ? new VerifiedRemoval.Kept($"{name} is not the copy this run created; it stays") : new VerifiedRemoval.Gone();
        }

        var removed = BeneathWrites.UnlinkAt(folder.Descriptor, name, directory: false);
        _onArchiveStep(ArchiveFileStep.OwnCopyRemoved, Path.Combine(folder.Path, name));
        return removed.Failed ? new VerifiedRemoval.Kept($"{name} could not be removed (errno {removed.Errno})") : new VerifiedRemoval.Removed();
    }

    /// <summary>Linux (own review round, security m2): only a REGULAR file is renamed — never a folder or a link — and the entry the
    /// new name holds afterwards must be the file that was checked (its device and inode).</summary>
    [SupportedOSPlatform("linux")]
    private NoReplaceRename RenameLinux(Located at, string newName) =>
        WithChain(Checked(at), readable: false, folder =>
        {
            var before = BeneathWrites.Stat(folder, at.Name);
            return !before.Known ? new NoReplaceRename.Gone()
                : !before.IsRegular ? new NoReplaceRename.Refused($"{at.Name} is not a regular file (a folder or a link), never renamed")
                : RenamedLinux(folder, at.Name, newName, before);
        }, (errno, level) => errno == BeneathWrites.NoEntry ? new NoReplaceRename.Gone() : new NoReplaceRename.Refused($"{LinkOrNotAFolder} (at {level})"));

    [SupportedOSPlatform("linux")]
    private NoReplaceRename RenamedLinux(int folder, string name, string newName, BeneathWrites.LinuxStatus before)
    {
        var renamed = BeneathWrites.RenameNoReplace(folder, name, newName);
        _onArchiveStep(ArchiveFileStep.Renamed, newName);
        return renamed.Failed || BeneathWrites.Stat(folder, newName).SameFile(before)
            ? RenameAnswer(renamed)
            : new NoReplaceRename.Refused($"the entry renamed to {newName} is not the file that was checked (it changed in between); it keeps that name and is reported");
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
    private VerifiedRemoval RemoveVerifiedLinux(Located at, string expected) =>
        WithChain(Checked(at), readable: false, folder =>
        {
            return VerifyThenUnlink(folder, at.Name, expected);
        }, (errno, level) => errno == BeneathWrites.NoEntry ? new VerifiedRemoval.Gone() : new VerifiedRemoval.Refused($"{LinkOrNotAFolder} (at {level})"));

    /// <summary>Linux: the file opened from its folder's descriptor, judged, leased, hashed; only an equal hash — the lease still
    /// whole and the name still naming THAT file — unlinks it. Residual: between that check and the unlink the name could be renamed
    /// over by another process of this account; the archive never does, and an agent writes by appending or creating, not by
    /// renaming onto a quarantine name.</summary>
    [SupportedOSPlatform("linux")]
    private VerifiedRemoval VerifyThenUnlink(int folder, string name, string expected)
    {
        using var opened = BeneathWrites.OpenReadAt(folder, name);
        return opened switch
        {
            NativeOpen.Opened file => LeasedThenUnlink(folder, name, expected, file.Handle),
            NativeOpen.Failed { Error: BeneathWrites.NoEntry } => new VerifiedRemoval.Gone(),
            NativeOpen.Failed failed => new VerifiedRemoval.Kept($"{name} could not be opened (errno {failed.Error})"),
            _ => new VerifiedRemoval.Kept(name),
        };
    }

    [SupportedOSPlatform("linux")]
    private VerifiedRemoval LeasedThenUnlink(int folder, string name, string expected, SafeFileHandle handle)
    {
        var descriptor = BeneathWrites.Descriptor(handle);
        var status = BeneathWrites.Stat(descriptor, string.Empty);
        _onArchiveStep(ArchiveFileStep.RemovalOpened, name);
        return !status.IsPlainFile ? new VerifiedRemoval.Kept($"{name} is not a plain file of one link")
            : BeneathWrites.TakeWriteLease(descriptor) is { Failed: true } lease ? new VerifiedRemoval.Kept($"{name} is open elsewhere (no write lease: errno {lease.Errno}) — a writer could still add to it; it stays")
            : HashThenUnlink(folder, name, expected, handle, status);
    }

    [SupportedOSPlatform("linux")]
    private VerifiedRemoval HashThenUnlink(int folder, string name, string expected, SafeFileHandle handle, BeneathWrites.LinuxStatus opened)
    {
        var stream = new FileStream(handle, FileAccess.Read, bufferSize: 0);
        var (sha, _) = Hash(stream, ArchiveFileStep.RemovalHashChunk, name);
        _onArchiveStep(ArchiveFileStep.RemovalHashed, name);
        var problem = ArchiveSourceRules.LinuxRemovalProblem(
            name,
            string.Equals(sha, expected, StringComparison.OrdinalIgnoreCase),
            BeneathWrites.LeaseHeld(BeneathWrites.Descriptor(handle)),
            BeneathWrites.Stat(folder, name).SameFile(opened));
        return problem.Length > 0 ? new VerifiedRemoval.Kept(problem) : Unlinked(BeneathWrites.UnlinkAt(folder, name, directory: false), name);
    }

    private VerifiedRemoval Unlinked(BeneathWrites.Native removed, string name)
    {
        _onArchiveStep(ArchiveFileStep.Removed, name);
        return removed.Failed ? new VerifiedRemoval.Kept($"{name} could not be removed (errno {removed.Errno})") : new VerifiedRemoval.Removed();
    }

    [SupportedOSPlatform("linux")]
    private VerifiedRemoval RemoveFolderLinux(Located at) =>
        WithChain(Checked(at), readable: false, folder =>
        {
            var removed = BeneathWrites.UnlinkAt(folder, at.Name, directory: true);
            _onArchiveStep(ArchiveFileStep.FolderRemoved, at.Name);
            return FolderAnswer(removed, at.Name);
        }, (errno, level) => errno == BeneathWrites.NoEntry ? new VerifiedRemoval.Gone() : new VerifiedRemoval.Refused($"{LinkOrNotAFolder} (at {level})"));

    private static VerifiedRemoval FolderAnswer(BeneathWrites.Native removed, string name) => removed switch
    {
        { Failed: false } => new VerifiedRemoval.Removed(),
        { Errno: BeneathWrites.NoEntry } => new VerifiedRemoval.Gone(),
        { Errno: BeneathWrites.NotEmpty or BeneathWrites.Exists } => new VerifiedRemoval.Kept($"{name} is not empty"),
        _ => new VerifiedRemoval.Kept($"{name} could not be removed as an empty folder (errno {removed.Errno})"),
    };

    /// <summary>Linux: a file along its real path, no link at any level, hashed when it is a plain file (the archived copy).</summary>
    [SupportedOSPlatform("linux")]
    private FileHash HashFileLinux(Located at) =>
        WithChain(at, readable: false, folder => BeneathWrites.OpenReadAt(folder, at.Name) switch
        {
            NativeOpen.Opened opened => HashedThrough(opened.Handle, at.RealPath),
            NativeOpen.Failed { Error: BeneathWrites.NoEntry } => new FileHash.Gone(),
            NativeOpen.Failed failed => new FileHash.Unreadable($"{at.Name} could not be opened (errno {failed.Error})"),
            _ => new FileHash.Unreadable(at.Name),
        }, (errno, level) => errno == BeneathWrites.NoEntry ? new FileHash.Gone() : new FileHash.Unreadable($"{LinkOrNotAFolder} (at {level})"));
}
