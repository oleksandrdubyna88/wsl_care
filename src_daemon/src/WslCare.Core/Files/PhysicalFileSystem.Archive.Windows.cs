using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

using Microsoft.Win32.SafeHandles;

namespace WslCare.Core.Files;

/// <summary>Plan §15r E9.S2a — the Windows half of the archive seam (<see cref="IArchiveFiles"/>): handles opened as themselves, by
/// the REAL path the policy judged, and asked afterwards where they really are (a link swapped in anywhere — the root's ancestors
/// included — makes the act refuse); the destination's folders held; every failure a closed answer, never an exception (own review
/// round M1, security m4).</summary>
public sealed partial class PhysicalFileSystem
{
    /// <summary>A Windows folder held — or why it could not be (own review round m4: never a dead handle with a sentence).</summary>
    private abstract record HeldLevel
    {
        private HeldLevel()
        {
        }

        public sealed record Held(SafeFileHandle Handle) : HeldLevel;

        public sealed record Refused(string Why) : HeldLevel;

        public string Problem => this is Refused refused ? refused.Why : string.Empty;
    }

    [SupportedOSPlatform("windows")]
    private SourceOpen OpenSourceWindows(Located at)
    {
        _onArchiveStep(ArchiveFileStep.PathChecked, at.Name);
        var opened = BeneathWrites.OpenSourceWindows(at.RealPath);
        return opened switch
        {
            NativeOpen.Opened file => JudgedSourceWindows(new FileStream(file.Handle, FileAccess.Read, bufferSize: 1), at),
            NativeOpen.Failed { Error: BeneathWrites.WindowsNotFound or BeneathWrites.WindowsPathNotFound } => new SourceOpen.Gone(),
            NativeOpen.Failed failed => new SourceOpen.Refused($"{at.Name} could not be opened (error {failed.Error})"),
            _ => new SourceOpen.Refused(at.Name),
        };
    }

    [SupportedOSPlatform("windows")]
    private SourceOpen JudgedSourceWindows(FileStream stream, Located at)
    {
        var info = BeneathWrites.Describe(stream.SafeFileHandle);
        var problem = ArchiveSourceRules.WindowsProblem(at.Name, new ArchiveSourceRules.WindowsSource(info.Known, info.Attributes, info.Links, OwnerOf(stream)), ThisAccount()) is { Length: > 0 } bad
            ? bad
            : NotInPlace(stream.SafeFileHandle, at);
        if (problem.Length > 0)
        {
            stream.Dispose();
            return new SourceOpen.Refused(problem);
        }

        _onArchiveStep(ArchiveFileStep.SourceOpened, at.Name);
        return new SourceOpen.Opened(stream, stream.Length, File.GetLastWriteTimeUtc(stream.SafeFileHandle));
    }

    /// <summary>The SID owning the open file, read through its handle (gate round finding 5); empty when it could not be read —
    /// which the source rules refuse.</summary>
    [SupportedOSPlatform("windows")]
    private static string OwnerOf(FileStream stream)
    {
        try
        {
            return stream.GetAccessControl().GetOwner(typeof(SecurityIdentifier))?.Value ?? string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        {
            return string.Empty;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string ThisAccount() => WindowsIdentity.GetCurrent().User?.Value ?? string.Empty;

    /// <summary>Gate round finding 3, own review round security m1: where the OPEN file really is, against the REAL path the policy
    /// judged — a link swapped in anywhere on the way after the judgement (the open is by path on Windows) leads the open
    /// elsewhere, and that file is never acted on. Empty when it is in place. A mapped network drive's answer comes under its UNC
    /// root, which is the same place (<see cref="NetworkPaths.InPlace"/>, the E9 live gate 2026-10-10). Residual: a short (8.3)
    /// spelling refuses.</summary>
    [SupportedOSPlatform("windows")]
    private static string NotInPlace(SafeFileHandle opened, Located at) =>
        NetworkPaths.InPlace(BeneathWrites.FinalPath(opened), at.RealPath, NetworkPaths.NetworkRootOf)
            ? string.Empty
            : $"{at.Name} was reached through a link (the open file is not where its path says), never followed";

    /// <summary>Windows (gate round findings 3 and 4): the base held by its REAL path and checked in place, then each level held — a
    /// handle that refuses the folder's and its parents' rename while open, so a level checked once stays the folder that was
    /// checked; a new level's entry flushed in its held parent; the last level's handle kept in the <see cref="OpenedFolder"/>.</summary>
    [SupportedOSPlatform("windows")]
    private FolderBeneath FolderWindows(string baseFolder, IReadOnlyList<string> levels, bool create)
    {
        var at = Real(baseFolder) is RealPathResult.Resolved resolved ? FromFileSystemRoot(resolved.Path) : Located.Refused($"{baseFolder} could not be resolved");
        return at.Problem.Length > 0 ? new FolderBeneath.Refused(at.Problem) : HeldBase(at) switch
        {
            HeldLevel.Held held => LevelsWindows(held.Handle, at.RealPath, levels, create),
            HeldLevel.Refused refused => MissingOr(at.RealPath, create, $"{baseFolder}: {refused.Why}"),
            _ => new FolderBeneath.Refused(baseFolder),
        };
    }

    /// <summary>The base held and found where its real path says.</summary>
    [SupportedOSPlatform("windows")]
    private static HeldLevel HeldBase(Located at)
    {
        var held = HeldFolder(at.RealPath);
        if (held is not HeldLevel.Held { Handle: var handle } || NotInPlace(handle, at) is not { Length: > 0 } moved)
        {
            return held;
        }

        handle.Dispose();
        return new HeldLevel.Refused(moved);
    }

    [SupportedOSPlatform("windows")]
    private FolderBeneath LevelsWindows(SafeFileHandle current, string path, IReadOnlyList<string> levels, bool create)
    {
        foreach (var level in levels)
        {
            HeldLevel next;
            try
            {
                next = create ? LevelWindows(current, Path.Combine(path, level)) : HeldFolder(Path.Combine(path, level));
            }
            finally
            {
                current.Dispose();
            }

            if (next is not HeldLevel.Held held)
            {
                return MissingOr(Path.Combine(path, level), create, $"{next.Problem} (at {level})");
            }

            (current, path) = (held.Handle, Path.Combine(path, level));
            _onArchiveStep(ArchiveFileStep.FolderLevelReady, path);
        }

        return new FolderBeneath.Ready(new OpenedFolder(path, current));
    }

    /// <summary>A level that could not be held: MISSING when the reader asked for existing levels only and nothing is there.</summary>
    private static FolderBeneath MissingOr(string path, bool create, string why) =>
        !create && !Path.Exists(path) ? new FolderBeneath.Missing() : new FolderBeneath.Refused(why);

    /// <summary>One level below a held parent: created when missing — a refusal, never an exception, when it cannot be (a file of
    /// that name, a denied access: own review round M1, security m4) — its entry flushed in the parent, then held itself.</summary>
    [SupportedOSPlatform("windows")]
    private HeldLevel LevelWindows(SafeFileHandle parent, string path)
    {
        if (Path.Exists(path))
        {
            return HeldFolder(path);
        }

        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new HeldLevel.Refused($"it could not be created ({e.Message})");
        }

        var synced = SyncedParentWindows(parent, path);
        return synced != 0 ? new HeldLevel.Refused($"its new entry could not be flushed (error {synced})") : HeldFolder(path);
    }

    [SupportedOSPlatform("windows")]
    private int SyncedParentWindows(SafeFileHandle parent, string path)
    {
        // The E9 live gate (2026-10-10): over SMB a folder handle has no flush (error 1) — NetworkPaths says why that is safe.
        var error = NetworkPaths.FolderFlushed(BeneathWrites.FlushWindows(parent), NetworkPaths.IsRemote(path, NetworkPaths.NetworkRootOf));
        _onArchiveStep(ArchiveFileStep.FolderLevelSynced, path);
        return error;
    }

    /// <summary>The folder at <paramref name="path"/> held (<see cref="BeneathWrites.HoldFolderWindows"/>) when it is a folder and
    /// no reparse point; otherwise why not, and nothing held.</summary>
    [SupportedOSPlatform("windows")]
    private static HeldLevel HeldFolder(string path)
    {
        var opened = BeneathWrites.HoldFolderWindows(path);
        if (opened is NativeOpen.Opened { Handle: var handle } && BeneathWrites.Describe(handle).Is(BeneathWrites.WindowsDirectory))
        {
            return new HeldLevel.Held(handle);
        }

        opened.Dispose();
        return new HeldLevel.Refused(opened is NativeOpen.Failed failed ? $"it could not be opened (error {failed.Error})" : LinkOrNotAFolder);
    }

    private ExclusiveFile CreateWindows(OpenedFolder folder, string name)
    {
        var path = Path.Combine(folder.Path, name);
        try
        {
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough);
            _onArchiveStep(ArchiveFileStep.ExclusiveCreated, path);
            return new ExclusiveFile.Created(stream, OperatingSystem.IsWindows() ? BeneathWrites.Describe(stream.SafeFileHandle).Identity : new FileIdentity(0, 0));
        }
        catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && Path.Exists(path))
        {
            return new ExclusiveFile.Exists();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new ExclusiveFile.Refused($"{name} could not be created ({e.Message})");
        }
    }

    /// <summary>Windows: read back past the system cache (<c>FILE_FLAG_NO_BUFFERING</c>) into a sector-aligned buffer.</summary>
    private FileHash ReadBackWindows(OpenedFolder folder, string name) =>
        OperatingSystem.IsWindows() ? HashUnbufferedAt(Path.Combine(folder.Path, name), name) : new FileHash.Unreadable(NotThisOs);

    /// <summary>The archived copy on Windows: opened by its real path, a plain file in place, hashed past the cache.</summary>
    private FileHash HashFileWindows(Located at) =>
        OperatingSystem.IsWindows() ? HashUnbufferedAt(at.RealPath, at.Name) : new FileHash.Unreadable(NotThisOs);

    [SupportedOSPlatform("windows")]
    private FileHash HashUnbufferedAt(string path, string name)
    {
        using var opened = BeneathWrites.OpenWindows(path, delete: false, unbuffered: true);
        return opened switch
        {
            NativeOpen.Opened file when BeneathWrites.Describe(file.Handle).IsPlainFile => HashedUnbuffered(file.Handle, path),
            NativeOpen.Opened => new FileHash.Unreadable($"{name} is not a plain file of one link"),
            NativeOpen.Failed { Error: BeneathWrites.WindowsNotFound or BeneathWrites.WindowsPathNotFound } => new FileHash.Gone(),
            _ => new FileHash.Unreadable($"{name} could not be opened (error {opened.ErrorCode})"),
        };
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

    /// <summary>Windows (own review round M1, M2): the archive's own copy removed THROUGH one <c>DELETE</c> handle, only while the name
    /// still names the file this run created (its volume serial and file index); still open (by the caller's create) → kept, never
    /// an exception. The classic disposition is allowed here (the base may be a share without POSIX deletes).</summary>
    private VerifiedRemoval UnlinkOwnWindows(OpenedFolder folder, string name, FileIdentity created)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new VerifiedRemoval.Refused(NotThisOs);
        }

        var path = Path.Combine(folder.Path, name);
        using var opened = BeneathWrites.OpenToChangeWindows(path);
        return opened switch
        {
            NativeOpen.Opened file => OwnCopyThrough(file.Handle, path, created),
            NativeOpen.Failed { Error: BeneathWrites.WindowsNotFound or BeneathWrites.WindowsPathNotFound } => new VerifiedRemoval.Gone(),
            NativeOpen.Failed { Error: BeneathWrites.WindowsSharing } => new VerifiedRemoval.Kept($"{name} is still open; it stays"),
            _ => new VerifiedRemoval.Kept($"{name} could not be opened (error {opened.ErrorCode}); it stays"),
        };
    }

    [SupportedOSPlatform("windows")]
    private VerifiedRemoval OwnCopyThrough(SafeFileHandle handle, string path, FileIdentity created)
    {
        var info = BeneathWrites.Describe(handle);
        if (!info.IsPlainFile || info.Identity != created)
        {
            return new VerifiedRemoval.Kept($"{Path.GetFileName(path)} is not the copy this run created; it stays");
        }

        var error = BeneathWrites.MarkForDeletion(handle, classicAllowed: true);
        _onArchiveStep(ArchiveFileStep.OwnCopyRemoved, path);
        return error == 0 ? new VerifiedRemoval.Removed() : new VerifiedRemoval.Kept($"{Path.GetFileName(path)} could not be marked for deletion (error {error}); it stays");
    }

    /// <summary>Windows (gate round finding 3): its folder HELD first (so neither it nor a folder above it can be renamed while the
    /// rename runs), the file opened as itself (its reparse point never followed), found in place, and renamed THROUGH that handle
    /// without replacing.</summary>
    private NoReplaceRename RenameWindows(Located at, string newName) =>
        OperatingSystem.IsWindows() ? OpenedForRename(at, newName) : new NoReplaceRename.Refused(NotThisOs);

    [SupportedOSPlatform("windows")]
    private NoReplaceRename OpenedForRename(Located at, string newName)
    {
        _onArchiveStep(ArchiveFileStep.PathChecked, at.Name);
        var folder = Path.GetDirectoryName(at.RealPath) ?? at.RealPath;
        using var held = BeneathWrites.HoldFolderWindows(folder);
        if (held is NativeOpen.Failed heldFailed)
        {
            return RenameAnswerWindows(heldFailed.Error);
        }

        using var opened = BeneathWrites.OpenToChangeWindows(at.RealPath);
        return opened switch
        {
            NativeOpen.Opened file => RenamedThrough(file.Handle, at, Path.Combine(folder, newName)),
            NativeOpen.Failed failed => RenameAnswerWindows(failed.Error),
            _ => new NoReplaceRename.Refused(at.Name),
        };
    }

    [SupportedOSPlatform("windows")]
    private NoReplaceRename RenamedThrough(SafeFileHandle handle, Located at, string destination)
    {
        var problem = !BeneathWrites.Describe(handle).Is(0) ? $"{at.Name} is a link or a folder, never renamed" : NotInPlace(handle, at);
        if (problem.Length > 0)
        {
            return new NoReplaceRename.Refused($"{problem}; nothing was renamed");
        }

        var error = BeneathWrites.RenameNoReplaceWindows(handle, destination);
        _onArchiveStep(ArchiveFileStep.Renamed, Path.GetFileName(destination));
        return RenameAnswerWindows(error);
    }

    private static NoReplaceRename RenameAnswerWindows(int error) => error switch
    {
        0 => new NoReplaceRename.Renamed(),
        BeneathWrites.WindowsFileExists or BeneathWrites.WindowsAlreadyExists => new NoReplaceRename.NameTaken(),
        BeneathWrites.WindowsNotFound or BeneathWrites.WindowsPathNotFound => new NoReplaceRename.Gone(),
        _ => new NoReplaceRename.Refused($"the rename failed (error {error}); nothing was renamed"),
    };

    /// <summary>Windows (review B2): ONE handle, <c>DELETE | READ</c>, sharing READ only — nobody writes, renames or deletes it while
    /// it is held; found in place; the bytes hashed through it; the POSIX delete disposition set ONLY after they are equal. A stop
    /// anywhere before — an exception, a kill — closes the handle with no disposition, and the file stays. Never
    /// <c>DeleteOnClose</c>, never the classic disposition under an agent's folder (own review round m2).</summary>
    private VerifiedRemoval RemoveVerifiedWindows(Located at, string expected) =>
        OperatingSystem.IsWindows() ? OpenedForRemoval(at, expected) : new VerifiedRemoval.Refused(NotThisOs);

    [SupportedOSPlatform("windows")]
    private VerifiedRemoval OpenedForRemoval(Located at, string expected)
    {
        _onArchiveStep(ArchiveFileStep.PathChecked, at.Name);
        using var opened = BeneathWrites.OpenWindows(at.RealPath, delete: true, unbuffered: false);
        return opened switch
        {
            NativeOpen.Opened file => VerifyThenDispose(file.Handle, at, expected),
            NativeOpen.Failed { Error: BeneathWrites.WindowsNotFound or BeneathWrites.WindowsPathNotFound } => new VerifiedRemoval.Gone(),
            NativeOpen.Failed { Error: BeneathWrites.WindowsSharing } => new VerifiedRemoval.Kept($"{at.Name} is open in another process; it stays"),
            _ => new VerifiedRemoval.Kept($"{at.Name} could not be opened (error {opened.ErrorCode}); it stays"),
        };
    }

    [SupportedOSPlatform("windows")]
    private VerifiedRemoval VerifyThenDispose(SafeFileHandle handle, Located at, string expected)
    {
        _onArchiveStep(ArchiveFileStep.RemovalOpened, at.Name);
        var problem = !BeneathWrites.Describe(handle).IsPlainFile ? $"{at.Name} is not a plain file of one link" : NotInPlace(handle, at);
        if (problem.Length > 0)
        {
            return new VerifiedRemoval.Kept($"{problem}; it stays");
        }

        var stream = new FileStream(handle, FileAccess.Read, bufferSize: 0);
        var (sha, _) = Hash(stream, ArchiveFileStep.RemovalHashChunk, at.Name);
        _onArchiveStep(ArchiveFileStep.RemovalHashed, at.Name);
        return string.Equals(sha, expected, StringComparison.OrdinalIgnoreCase)
            ? Disposed(BeneathWrites.MarkForDeletion(handle, classicAllowed: false), at.Name)
            : new VerifiedRemoval.Kept($"{at.Name}'s bytes differ from its archived copy; it stays");
    }

    private VerifiedRemoval Disposed(int error, string name)
    {
        _onArchiveStep(ArchiveFileStep.Removed, name);
        return error == 0 ? new VerifiedRemoval.Removed() : new VerifiedRemoval.Kept($"{name} could not be marked for a POSIX delete (error {error}) — this file system may have none; it stays");
    }

    /// <summary>Windows (gate round finding 3): the folder opened as itself, found in place, and removed THROUGH that handle by its
    /// POSIX delete disposition — which the file system refuses for a folder that is not empty; never recursive.</summary>
    private VerifiedRemoval RemoveFolderWindows(Located at) =>
        OperatingSystem.IsWindows() ? OpenedFolderForRemoval(at) : new VerifiedRemoval.Refused(NotThisOs);

    [SupportedOSPlatform("windows")]
    private VerifiedRemoval OpenedFolderForRemoval(Located at)
    {
        _onArchiveStep(ArchiveFileStep.PathChecked, at.Name);
        using var opened = BeneathWrites.OpenToChangeWindows(at.RealPath);
        return opened switch
        {
            NativeOpen.Opened folder => FolderRemovedThrough(folder.Handle, at),
            NativeOpen.Failed { Error: BeneathWrites.WindowsNotFound or BeneathWrites.WindowsPathNotFound } => new VerifiedRemoval.Gone(),
            _ => new VerifiedRemoval.Kept($"{at.Name} could not be opened (error {opened.ErrorCode}); it stays"),
        };
    }

    [SupportedOSPlatform("windows")]
    private VerifiedRemoval FolderRemovedThrough(SafeFileHandle handle, Located at)
    {
        var problem = !BeneathWrites.Describe(handle).Is(BeneathWrites.WindowsDirectory) ? $"{at.Name} is not a folder (or is a link)" : NotInPlace(handle, at);
        if (problem.Length > 0)
        {
            return new VerifiedRemoval.Kept($"{problem}; it stays");
        }

        var error = BeneathWrites.MarkForDeletion(handle, classicAllowed: false);
        _onArchiveStep(ArchiveFileStep.FolderRemoved, at.Name);
        return error switch
        {
            0 => new VerifiedRemoval.Removed(),
            BeneathWrites.WindowsFolderNotEmpty => new VerifiedRemoval.Kept($"{at.Name} is not empty"),
            _ => new VerifiedRemoval.Kept($"{at.Name} could not be removed as an empty folder (error {error})"),
        };
    }
}
