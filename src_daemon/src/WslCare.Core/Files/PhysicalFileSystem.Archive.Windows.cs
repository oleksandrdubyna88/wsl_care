using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

using Microsoft.Win32.SafeHandles;

namespace WslCare.Core.Files;

/// <summary>Plan §15r E9.S2a — the Windows half of the archive seam (<see cref="IArchiveFiles"/>): handles opened as themselves,
/// checked for where they really are, the destination's folders held (gate round, findings 3 and 4).</summary>
public sealed partial class PhysicalFileSystem
{
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

        _onArchiveStep(ArchiveFileStep.PathChecked, below.Name);
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            return JudgedSourceWindows(stream, root, below);
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
    private SourceOpen JudgedSourceWindows(FileStream stream, string root, BelowRoot below)
    {
        var name = below.Name;
        var (ok, attributes, links) = BeneathWrites.Describe(stream.SafeFileHandle);
        var problem = ArchiveSourceRules.WindowsProblem(name, new ArchiveSourceRules.WindowsSource(ok, attributes, links, OwnerOf(stream)), ThisAccount()) is { Length: > 0 } bad
            ? bad
            : ReachedThroughALink(stream.SafeFileHandle, root, below);
        if (problem.Length > 0)
        {
            stream.Dispose();
            return new SourceOpen.Refused(problem);
        }

        _onArchiveStep(ArchiveFileStep.SourceOpened, name);
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

    /// <summary>Windows (gate round findings 3 and 4): the base and then each level HELD — a handle that refuses the folder's and
    /// its parents' rename while open, so a level checked once stays the folder that was checked; a new level's entry flushed in
    /// its held parent; the last level's handle kept in the <see cref="OpenedFolder"/> for the creates and the flush.</summary>
    [SupportedOSPlatform("windows")]
    private FolderBeneath FolderWindows(string baseFolder, IReadOnlyList<string> levels)
    {
        var (current, problem) = HeldFolder(baseFolder);
        if (problem.Length > 0)
        {
            return new FolderBeneath.Refused($"{baseFolder}: {problem}");
        }

        var path = baseFolder;
        foreach (var level in levels)
        {
            var next = LevelWindows(current, Path.Combine(path, level));
            current.Dispose();
            if (next.Problem.Length > 0)
            {
                return new FolderBeneath.Refused($"{next.Problem} (at {level})");
            }

            (current, path) = (next.Handle, Path.Combine(path, level));
            _onArchiveStep(ArchiveFileStep.FolderLevelReady, path);
        }

        var held = current;
        return new FolderBeneath.Ready(new OpenedFolder(path, -1, held, held.Dispose));
    }

    /// <summary>One level below a held parent: created when missing and its entry flushed in the parent, then held itself.</summary>
    [SupportedOSPlatform("windows")]
    private (SafeFileHandle Handle, string Problem) LevelWindows(SafeFileHandle parent, string path)
    {
        var created = !Path.Exists(path);
        if (created)
        {
            Directory.CreateDirectory(path);
        }

        var synced = created ? SyncedParentWindows(parent, path) : 0;
        return synced != 0 ? (new SafeFileHandle(), $"its new entry could not be flushed (error {synced})") : HeldFolder(path);
    }

    [SupportedOSPlatform("windows")]
    private int SyncedParentWindows(SafeFileHandle parent, string path)
    {
        var error = BeneathWrites.FlushWindows(parent);
        _onArchiveStep(ArchiveFileStep.FolderLevelSynced, path);
        return error;
    }

    /// <summary>The folder at <paramref name="path"/> held (<see cref="BeneathWrites.HoldFolderWindows"/>) when it is a folder and
    /// no reparse point; otherwise why not, and nothing held.</summary>
    [SupportedOSPlatform("windows")]
    private static (SafeFileHandle Handle, string Problem) HeldFolder(string path)
    {
        var (handle, error) = BeneathWrites.HoldFolderWindows(path);
        var problem = error != 0 ? $"it could not be opened (error {error})"
            : IsWindowsKind(handle, BeneathWrites.WindowsDirectory) ? string.Empty
            : LinkOrNotAFolder;
        if (problem.Length > 0)
        {
            handle.Dispose();
        }

        return (handle, problem);
    }

    /// <summary>Gate round finding 3: where the OPEN file really is, against where its path says — a folder on the way swapped for a
    /// link after the reparse check (the open is by path on Windows) leads the open elsewhere, and that file is never acted on.</summary>
    [SupportedOSPlatform("windows")]
    private static string ReachedThroughALink(SafeFileHandle opened, string root, BelowRoot below)
    {
        using var rootHandle = BeneathWrites.OpenForName(root);
        var expected = Path.Join([BeneathWrites.FinalPath(rootHandle), .. below.Folders, below.Name]);
        var actual = BeneathWrites.FinalPath(opened);
        return actual.Length > 0 && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase) ? string.Empty
            : $"{below.Name} was reached through a link (the open file is not where its path says), never followed";
    }

    private ExclusiveFile CreateWindows(OpenedFolder folder, string name)
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
    private FileHash ReadBackWindows(OpenedFolder folder, string name)
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

    private VerifiedRemoval UnlinkOwnWindows(OpenedFolder folder, string name)
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

    /// <summary>Windows (gate round finding 3): its folder HELD first (so neither it nor a folder above it can be renamed while the
    /// rename runs), the file opened as itself (its reparse point never followed), asked where it really is, and renamed THROUGH
    /// that handle without replacing — a folder swapped for a link before the hold makes it refuse.</summary>
    private NoReplaceRename RenameWindows(string root, BelowRoot below, string newName) =>
        !OperatingSystem.IsWindows() ? new NoReplaceRename.Refused(NotThisOs)
        : ReparseOnTheWay(root, [.. below.Folders, below.Name]) is { Length: > 0 } link ? new NoReplaceRename.Refused($"{LinkOrNotAFolder} (at {link})")
        : OpenedForRename(root, below, newName);

    [SupportedOSPlatform("windows")]
    private NoReplaceRename OpenedForRename(string root, BelowRoot below, string newName)
    {
        _onArchiveStep(ArchiveFileStep.PathChecked, below.Name);
        var folder = Path.Combine([root, .. below.Folders]);
        var (held, heldError) = BeneathWrites.HoldFolderWindows(folder);
        using (held)
        {
            if (heldError != 0)
            {
                return RenameAnswerWindows(heldError);
            }

            var (handle, error) = BeneathWrites.OpenToChangeWindows(Path.Combine(folder, below.Name));
            using (handle)
            {
                return error == 0 ? RenamedThrough(handle, root, below, Path.Combine(folder, newName)) : RenameAnswerWindows(error);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private NoReplaceRename RenamedThrough(SafeFileHandle handle, string root, BelowRoot below, string destination)
    {
        var problem = !IsWindowsKind(handle, 0) ? $"{below.Name} is a link or a folder, never renamed" : ReachedThroughALink(handle, root, below);
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

    /// <summary>Whether the open handle is no reparse point and is a folder (<paramref name="folder"/> = the directory attribute) or
    /// a file (0).</summary>
    [SupportedOSPlatform("windows")]
    private static bool IsWindowsKind(SafeFileHandle handle, uint folder) =>
        BeneathWrites.Describe(handle) is (true, var attributes, _) && (attributes & (BeneathWrites.WindowsReparsePoint | BeneathWrites.WindowsDirectory)) == folder;

    /// <summary>Windows (review B2): ONE handle, <c>DELETE | READ</c>, sharing READ only — nobody writes, renames or deletes it while
    /// it is held; the bytes hashed through it; the delete disposition set ONLY after they are equal. A stop anywhere before —
    /// an exception, a kill — closes the handle with no disposition, and the file stays. Never <c>DeleteOnClose</c>.</summary>
    private VerifiedRemoval RemoveVerifiedWindows(string root, BelowRoot below, string expected) =>
        !OperatingSystem.IsWindows() ? new VerifiedRemoval.Refused(NotThisOs)
        : ReparseOnTheWay(root, below.Folders) is { Length: > 0 } link ? new VerifiedRemoval.Refused($"{LinkOrNotAFolder} (at {link})")
        : OpenedForRemoval(root, below, expected);

    [SupportedOSPlatform("windows")]
    private VerifiedRemoval OpenedForRemoval(string root, BelowRoot below, string expected)
    {
        var name = below.Name;
        _onArchiveStep(ArchiveFileStep.PathChecked, name);
        var (handle, error) = BeneathWrites.OpenWindows(Path.Combine([root, .. below.Folders, name]), delete: true, unbuffered: false);
        using (handle)
        {
            return error switch
            {
                0 => VerifyThenDispose(handle, root, below, expected),
                BeneathWrites.WindowsNotFound or BeneathWrites.WindowsPathNotFound => new VerifiedRemoval.Gone(),
                BeneathWrites.WindowsSharing => new VerifiedRemoval.Kept($"{name} is open in another process; it stays"),
                _ => new VerifiedRemoval.Kept($"{name} could not be opened (error {error}); it stays"),
            };
        }
    }

    [SupportedOSPlatform("windows")]
    private VerifiedRemoval VerifyThenDispose(SafeFileHandle handle, string root, BelowRoot below, string expected)
    {
        var name = below.Name;
        _onArchiveStep(ArchiveFileStep.RemovalOpened, name);
        var problem = !IsPlainWindowsFile(handle) ? $"{name} is not a plain file of one link" : ReachedThroughALink(handle, root, below);
        if (problem.Length > 0)
        {
            return new VerifiedRemoval.Kept($"{problem}; it stays");
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

    /// <summary>Windows (gate round finding 3): the folder opened as itself, asked where it really is, and removed THROUGH that
    /// handle by its delete disposition — which the file system refuses for a folder that is not empty; never recursive.</summary>
    private VerifiedRemoval RemoveFolderWindows(string root, BelowRoot below) =>
        !OperatingSystem.IsWindows() ? new VerifiedRemoval.Refused(NotThisOs)
        : ReparseOnTheWay(root, [.. below.Folders, below.Name]) is { Length: > 0 } link ? new VerifiedRemoval.Refused($"{LinkOrNotAFolder} (at {link})")
        : OpenedFolderForRemoval(root, below);

    [SupportedOSPlatform("windows")]
    private VerifiedRemoval OpenedFolderForRemoval(string root, BelowRoot below)
    {
        _onArchiveStep(ArchiveFileStep.PathChecked, below.Name);
        var (handle, error) = BeneathWrites.OpenToChangeWindows(Path.Combine([root, .. below.Folders, below.Name]));
        using (handle)
        {
            return error switch
            {
                0 => FolderRemovedThrough(handle, root, below),
                BeneathWrites.WindowsNotFound or BeneathWrites.WindowsPathNotFound => new VerifiedRemoval.Gone(),
                _ => new VerifiedRemoval.Kept($"{below.Name} could not be opened (error {error}); it stays"),
            };
        }
    }

    [SupportedOSPlatform("windows")]
    private VerifiedRemoval FolderRemovedThrough(SafeFileHandle handle, string root, BelowRoot below)
    {
        var problem = !IsWindowsKind(handle, BeneathWrites.WindowsDirectory) ? $"{below.Name} is not a folder (or is a link)" : ReachedThroughALink(handle, root, below);
        if (problem.Length > 0)
        {
            return new VerifiedRemoval.Kept($"{problem}; it stays");
        }

        var error = BeneathWrites.MarkForDeletion(handle);
        _onArchiveStep(ArchiveFileStep.FolderRemoved, below.Name);
        return error switch
        {
            0 => new VerifiedRemoval.Removed(),
            BeneathWrites.WindowsFolderNotEmpty => new VerifiedRemoval.Kept($"{below.Name} is not empty"),
            _ => new VerifiedRemoval.Kept($"{below.Name} could not be removed as an empty folder (error {error})"),
        };
    }
}
