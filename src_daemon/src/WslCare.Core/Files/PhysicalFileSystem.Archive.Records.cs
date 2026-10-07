using System.Runtime.Versioning;

using Microsoft.Win32.SafeHandles;

using WslCare.Core.Files.Deletion;

namespace WslCare.Core.Files;

/// <summary>
/// Plan §15r E9.S2b — the base's own records through the seam: the month index appended and flushed, a small file of the base
/// read under a cap, a file removed only while it is what the caller judged — all in a folder the seam holds, never through a
/// link, a plain file of one link only. The base is a user-chosen, possibly shared folder: what is read from it is untrusted
/// input, and an append counts only once it is on the disk.
/// </summary>
public sealed partial class PhysicalFileSystem
{
    private const string NotPlain = "is not a plain file of one link (a link, a folder or a hard link), never written or read";

    public DurableAppend AppendDurably(BeneathFolder folder, string name, ReadOnlySpan<byte> bytes, DeletionScope scope)
    {
        if (folder is not OpenedFolder own)
        {
            return new DurableAppend.Refused(ForeignFolder(folder));
        }

        var copy = bytes.ToArray();
        var created = ReadCapped(folder, name, 0) is FileReadResult.Missing;
        var appended = own.With(() => AppendIn(own, name, copy, scope), why => new DurableAppend.Refused(why));
        return appended is DurableAppend.Appended && created ? Entered(FlushFolder(folder), name) : appended;
    }

    /// <summary>Review m-2: a file the append CREATED counts only once its entry is flushed into its folder too.</summary>
    private static DurableAppend Entered(FolderFlush flushed, string name) => flushed is FolderFlush.Failed failed
        ? new DurableAppend.Failed($"{name} was created but its folder could not be flushed ({failed.Why})")
        : new DurableAppend.Appended();

    public FileReadResult ReadCapped(BeneathFolder folder, string name, int maxBytes) =>
        folder is OpenedFolder own ? own.With(() => ReadCappedIn(own, name, maxBytes), why => new FileReadResult.Unreadable(why)) : new FileReadResult.Unreadable(ForeignFolder(folder));

    public VerifiedRemoval RemoveIfUnchanged(BeneathFolder folder, string name, string expectedSha256, DeletionScope scope) =>
        folder is OpenedFolder own ? own.With(() => RemoveUnchangedIn(own, name, expectedSha256, scope), why => new VerifiedRemoval.Refused(why)) : new VerifiedRemoval.Refused(ForeignFolder(folder));

    private DurableAppend AppendIn(OpenedFolder folder, string name, byte[] bytes, DeletionScope scope)
    {
        var problem = NameProblem(name) is { Length: > 0 } bad ? bad : Why(JudgeArchive(FileOperation.Create, Path.Combine(folder.Path, name), string.Empty, scope).Verdict);
        return problem.Length > 0 ? new DurableAppend.Refused(problem)
            : OperatingSystem.IsLinux() ? AppendLinux(folder, name, bytes)
            : OperatingSystem.IsWindows() ? AppendWindows(folder, name, bytes)
            : new DurableAppend.Refused(NotThisOs);
    }

    private FileReadResult ReadCappedIn(OpenedFolder folder, string name, int maxBytes) =>
        NameProblem(name) is { Length: > 0 } bad ? new FileReadResult.Unreadable(bad)
        : OperatingSystem.IsLinux() ? ReadCappedLinux(folder, name, maxBytes)
        : OperatingSystem.IsWindows() ? ReadCappedWindows(folder, name, maxBytes)
        : new FileReadResult.Unreadable(NotThisOs);

    private VerifiedRemoval RemoveUnchangedIn(OpenedFolder folder, string name, string expected, DeletionScope scope)
    {
        var problem = NameProblem(name) is { Length: > 0 } bad ? bad : Why(JudgeArchive(FileOperation.Delete, Path.Combine(folder.Path, name), string.Empty, scope).Verdict);
        return problem.Length > 0 ? new VerifiedRemoval.Refused(problem)
            : OperatingSystem.IsLinux() ? RemoveUnchangedLinux(folder, name, expected)
            : OperatingSystem.IsWindows() ? RemoveUnchangedWindows(folder, name, expected)
            : new VerifiedRemoval.Refused(NotThisOs);
    }

    /// <summary>The bytes written whole, the fault seam asked, then the file flushed to the disk — a failure of either is
    /// <see cref="DurableAppend.Failed"/>, never counted.</summary>
    private DurableAppend Written(FileStream stream, byte[] bytes, string path, bool torn)
    {
        try
        {
            // Review M3 (the history's rule, gate finding #6): torn remains of a writer that died mid-append are ended first, so
            // this line is never glued onto them and lost with them.
            stream.Write(torn ? [(byte)'\n', .. bytes] : bytes);
            _onArchiveStep(ArchiveFileStep.Appended, path);
            stream.Flush(flushToDisk: true);
            return new DurableAppend.Appended();
        }
        catch (IOException e)
        {
            return new DurableAppend.Failed($"{path}: the append or its flush failed ({e.Message})");
        }
    }

    /// <summary>At most <paramref name="maxBytes"/> of the stream; past it the file is refused whole.</summary>
    private static FileReadResult Capped(Stream stream, int maxBytes)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[Math.Min(maxBytes + 1, HashBuffer)];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maxBytes)
            {
                return new FileReadResult.Unreadable(FileReadResult.TooLarge(maxBytes));
            }
        }

        return new FileReadResult.Content(buffer.ToArray());
    }

    // ---- Linux -----------------------------------------------------------------------------------------------------------------

    [SupportedOSPlatform("linux")]
    private DurableAppend AppendLinux(OpenedFolder folder, string name, byte[] bytes)
    {
        using var opened = BeneathWrites.AppendAt(folder.Descriptor, name);
        if (opened is not NativeOpen.Opened file)
        {
            return new DurableAppend.Refused($"{name} could not be opened to append (errno {opened.ErrorCode}); nothing written");
        }

        using var stream = new FileStream(file.Handle, FileAccess.Write, bufferSize: 0);
        return BeneathWrites.Stat(BeneathWrites.Descriptor(file.Handle), string.Empty).IsPlainFile
            ? Written(stream, bytes, Path.Combine(folder.Path, name), EndsTorn(file.Handle))
            : new DurableAppend.Refused($"{name} {NotPlain}");
    }

    [SupportedOSPlatform("linux")]
    private static FileReadResult ReadCappedLinux(OpenedFolder folder, string name, int maxBytes)
    {
        using var opened = BeneathWrites.OpenReadAt(folder.Descriptor, name);
        return opened switch
        {
            NativeOpen.Opened file when BeneathWrites.Stat(BeneathWrites.Descriptor(file.Handle), string.Empty).IsPlainFile => ReadAll(file.Handle, maxBytes),
            NativeOpen.Opened => new FileReadResult.Unreadable($"{name} {NotPlain}"),
            NativeOpen.Failed { Error: BeneathWrites.NoEntry } => new FileReadResult.Missing(),
            _ => new FileReadResult.Unreadable($"{name} could not be opened (errno {opened.ErrorCode})"),
        };
    }

    /// <summary>Linux: hashed through its open descriptor, and unlinked only when equal and the name still names THAT file.</summary>
    [SupportedOSPlatform("linux")]
    private VerifiedRemoval RemoveUnchangedLinux(OpenedFolder folder, string name, string expected)
    {
        using var opened = BeneathWrites.OpenReadAt(folder.Descriptor, name);
        if (opened is not NativeOpen.Opened file)
        {
            return opened.ErrorCode == BeneathWrites.NoEntry ? new VerifiedRemoval.Gone() : new VerifiedRemoval.Kept($"{name} could not be opened (errno {opened.ErrorCode})");
        }

        var status = BeneathWrites.Stat(BeneathWrites.Descriptor(file.Handle), string.Empty);
        var (sha, _) = Hash(new FileStream(file.Handle, FileAccess.Read, bufferSize: 0), ArchiveFileStep.RemovalHashChunk, name);
        var same = status.IsPlainFile && string.Equals(sha, expected, StringComparison.OrdinalIgnoreCase) && BeneathWrites.Stat(folder.Descriptor, name).SameFile(status);
        return same ? Unlinked(BeneathWrites.UnlinkAt(folder.Descriptor, name, directory: false), name) : new VerifiedRemoval.Kept($"{name} changed since it was read; it stays");
    }

    // ---- Windows ---------------------------------------------------------------------------------------------------------------

    [SupportedOSPlatform("windows")]
    private DurableAppend AppendWindows(OpenedFolder folder, string name, byte[] bytes)
    {
        var path = Path.Combine(folder.Path, name);
        var torn = TornWindows(path);
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 1, FileOptions.WriteThrough);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new DurableAppend.Refused($"{name} could not be opened to append ({e.Message}); nothing written");
        }

        using (stream)
        {
            return BeneathWrites.Describe(stream.SafeFileHandle).IsPlainFile && InHeldFolder(stream.SafeFileHandle, folder, name)
                ? Written(stream, bytes, path, torn)
                : new DurableAppend.Refused($"{name} {NotPlain}");
        }
    }

    /// <summary>Whether the file ends torn, read before the append opens it (the side's lease makes this run its one writer).</summary>
    [SupportedOSPlatform("windows")]
    private static bool TornWindows(string path)
    {
        using var opened = BeneathWrites.OpenWindows(path, delete: false, unbuffered: false);
        return opened is NativeOpen.Opened file && EndsTorn(file.Handle);
    }

    [SupportedOSPlatform("windows")]
    private static FileReadResult ReadCappedWindows(OpenedFolder folder, string name, int maxBytes)
    {
        using var opened = BeneathWrites.OpenWindows(Path.Combine(folder.Path, name), delete: false, unbuffered: false);
        return opened switch
        {
            NativeOpen.Opened file when BeneathWrites.Describe(file.Handle).IsPlainFile && InHeldFolder(file.Handle, folder, name) => ReadAll(file.Handle, maxBytes),
            NativeOpen.Opened => new FileReadResult.Unreadable($"{name} {NotPlain}"),
            NativeOpen.Failed { Error: BeneathWrites.WindowsNotFound or BeneathWrites.WindowsPathNotFound } => new FileReadResult.Missing(),
            _ => new FileReadResult.Unreadable($"{name} could not be opened (error {opened.ErrorCode})"),
        };
    }

    /// <summary>Windows: one <c>DELETE | READ</c> handle sharing read only, hashed, the disposition set only when equal.</summary>
    [SupportedOSPlatform("windows")]
    private VerifiedRemoval RemoveUnchangedWindows(OpenedFolder folder, string name, string expected)
    {
        using var opened = BeneathWrites.OpenWindows(Path.Combine(folder.Path, name), delete: true, unbuffered: false);
        if (opened is not NativeOpen.Opened file)
        {
            return opened.ErrorCode is BeneathWrites.WindowsNotFound or BeneathWrites.WindowsPathNotFound ? new VerifiedRemoval.Gone() : new VerifiedRemoval.Kept($"{name} could not be opened (error {opened.ErrorCode})");
        }

        if (!BeneathWrites.Describe(file.Handle).IsPlainFile || !InHeldFolder(file.Handle, folder, name))
        {
            return new VerifiedRemoval.Kept($"{name} {NotPlain}");
        }

        var (sha, _) = Hash(new FileStream(file.Handle, FileAccess.Read, bufferSize: 0), ArchiveFileStep.RemovalHashChunk, name);
        return string.Equals(sha, expected, StringComparison.OrdinalIgnoreCase)
            ? Disposed(BeneathWrites.MarkForDeletion(file.Handle, classicAllowed: true), name)
            : new VerifiedRemoval.Kept($"{name} changed since it was read; it stays");
    }

    private static FileReadResult ReadAll(SafeFileHandle handle, int maxBytes)
    {
        var stream = new FileStream(handle, FileAccess.Read, bufferSize: 0);
        return Capped(stream, maxBytes);
    }

    /// <summary>Whether the open file really is <paramref name="name"/> in the held folder — not reached through a link at the name.</summary>
    [SupportedOSPlatform("windows")]
    private static bool InHeldFolder(SafeFileHandle opened, OpenedFolder folder, string name) =>
        BeneathWrites.FinalPath(opened) is { Length: > 0 } actual
        && string.Equals(actual, Path.Join(BeneathWrites.FinalPath(folder.Handle), name), StringComparison.OrdinalIgnoreCase);
}
