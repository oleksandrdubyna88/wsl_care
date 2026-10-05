using WslCare.Core.Files.Deletion;

namespace WslCare.Core.Files;

/// <summary>What creating a file EXCLUSIVELY produced (<see cref="IFileSystem.CreateFileExclusively"/>) — a closed set.</summary>
public abstract record ExclusiveCreate
{
    private ExclusiveCreate()
    {
    }

    /// <summary>The file did not exist and now holds the whole content, made visible in one step.</summary>
    public sealed record Created : ExclusiveCreate;

    /// <summary>A file of that name already existed; nothing was changed.</summary>
    public sealed record AlreadyExists : ExclusiveCreate;

    /// <summary>The deletion policy refused the path (or its temporary sibling); nothing was created.</summary>
    public sealed record Refused(string Reason) : ExclusiveCreate;
}

/// <summary>What reading a file produced: its bytes, nothing there, or a file that is there and cannot be read.</summary>
/// <remarks>Three facts, kept apart on purpose: a missing configuration layer is normal and silent, an
/// unreadable one is a configuration error the daemon must report (plan §15a #1).</remarks>
public abstract record FileReadResult
{
    private FileReadResult()
    {
    }

    public sealed record Content(byte[] Bytes) : FileReadResult;

    public sealed record Missing : FileReadResult;

    public sealed record Unreadable(string Reason) : FileReadResult;
}

/// <summary>What reading a link produced: its target, a path that is not a link (or not there), or a
/// link that is there and cannot be read — <c>/proc/[pid]/cwd</c> of another user's process, for one.</summary>
public abstract record LinkReadResult
{
    private LinkReadResult()
    {
    }

    public sealed record Target(string Path) : LinkReadResult;

    public sealed record NotALink : LinkReadResult;

    public sealed record Unreadable(string Reason) : LinkReadResult;
}

/// <summary>What measuring the filesystem holding a path produced (<c>statvfs</c> on Linux,
/// <c>GetDiskFreeSpaceEx</c> on Windows, through <see cref="DriveInfo"/>): its size, the free bytes,
/// the bytes an unprivileged user may still write — or why it could not be measured.</summary>
public abstract record VolumeReadResult
{
    private VolumeReadResult()
    {
    }

    /// <param name="TotalBytes">The filesystem's size.</param>
    /// <param name="FreeBytes">Free blocks, the reserve included (<c>f_bfree</c>).</param>
    /// <param name="AvailableBytes">What an unprivileged writer may still use (<c>f_bavail</c>, <c>df</c>'s "Avail").</param>
    public sealed record Measured(long TotalBytes, long FreeBytes, long AvailableBytes) : VolumeReadResult;

    public sealed record Unreadable(string Reason) : VolumeReadResult;
}

/// <summary>What measuring one file produced: its length and last write, nothing there, or a file that cannot be inspected.</summary>
public abstract record FileSizeResult
{
    private FileSizeResult()
    {
    }

    /// <param name="Bytes">Its length.</param>
    /// <param name="ModifiedAt">Its last write, UTC — how the health check tells a collector that still writes
    /// (sysstat, atop) from one that stopped.</param>
    public sealed record Measured(long Bytes, DateTimeOffset ModifiedAt) : FileSizeResult;

    public sealed record Missing : FileSizeResult;

    public sealed record Unreadable(string Reason) : FileSizeResult;
}

/// <summary>When a directory was last written (an entry created or removed in it) — how A14 tells the newest editor-server
/// builds from the old ones (E3.S2) — or nothing there, or a directory that cannot be inspected.</summary>
public abstract record DirectoryTimeResult
{
    private DirectoryTimeResult()
    {
    }

    public sealed record Measured(DateTimeOffset ModifiedAt) : DirectoryTimeResult;

    public sealed record Missing : DirectoryTimeResult;

    public sealed record Unreadable(string Reason) : DirectoryTimeResult;
}

/// <summary>Whether this process may create files in a directory — answered by trying (plan §15b #3: privilege
/// is the operating system's answer, not an id check of ours).</summary>
public abstract record WriteAccess
{
    private WriteAccess()
    {
    }

    public sealed record Writable : WriteAccess;

    public sealed record NotWritable(string Reason) : WriteAccess;
}

/// <summary>The ceiling on one walk of a tree (reliability rule: every wait has a ceiling): how many entries it
/// may visit and how long it may take. A walk that reaches either stops and says so.</summary>
public sealed record TreeLimits(int MaxEntries, TimeSpan MaxDuration);

/// <summary>What a bounded walk of a tree found.</summary>
public abstract record TreeMeasure
{
    private TreeMeasure()
    {
    }

    /// <param name="Bytes">The summed length of the files counted.</param>
    /// <param name="Files">How many files were counted.</param>
    /// <param name="Complete">The walk reached its end — <c>false</c> when a limit stopped it, and then
    /// <paramref name="Note"/> says which; the figures are a lower bound.</param>
    /// <param name="Note">Why it is incomplete; empty when complete.</param>
    public sealed record Measured(long Bytes, long Files, bool Complete, string Note) : TreeMeasure;

    public sealed record Missing : TreeMeasure;

    public sealed record Unreadable(string Reason) : TreeMeasure;
}

/// <summary>An exclusive lock file held — or busy because another process holds it.</summary>
public abstract record ExclusiveLock
{
    private ExclusiveLock()
    {
    }

    /// <summary>Held until <paramref name="Handle"/> is disposed, or the process dies (the OS releases it).</summary>
    public sealed record Held(IDisposable Handle) : ExclusiveLock;

    public sealed record Busy(string Reason) : ExclusiveLock;
}

/// <summary>
/// The one road to the disk for everything that removes or relocates (plan §15a C1).
/// </summary>
/// <remarks>
/// <para>Every delete and every move in the product passes through an implementation of this
/// interface, and the implementation asks ONE <see cref="DeletionPolicy"/> before touching
/// anything. An architecture test keeps <c>File.Delete</c>, <c>File.Move</c>,
/// <c>Directory.Delete</c>, <c>Directory.Move</c> and their <c>FileInfo</c>/<c>DirectoryInfo</c>
/// forms out of every other file under <c>src_daemon/src</c>, so a caller cannot forget to ask: there
/// is nothing else to call.</para>
/// <para>A refusal is a value, not an exception — the action that asked logs it and goes on
/// (plan §5: a failing action never ends a run). Infrastructure failures (I/O errors, permissions)
/// still throw, exactly as the C# doctrine asks.</para>
/// <para>Reads are here too, so a component that reads AND deletes has one dependency rather than
/// two, and so a test can hand every component the same temporary root.</para>
/// </remarks>
public interface IFileSystem
{
    FileReadResult ReadFile(string path);

    /// <summary>A file a caller NAMED, read only when it is a REGULAR file and only up to <paramref name="maxBytes"/> bytes
    /// actually read — a directory, FIFO, socket or device is <see cref="FileReadResult.Unreadable"/> at once, never waited
    /// on, and a larger file is refused, whatever its length claims (<see cref="RegularFiles"/>).</summary>
    FileReadResult ReadRegularFile(string path, int maxBytes);

    /// <summary>A file ROOT wrote under the state directory for another process to trust (the request files, E6.S0 review
    /// S1): read as <see cref="ReadRegularFile"/> does, never through a symbolic link, and on Linux only when the open
    /// descriptor's owner is the state's owner (root) and neither group nor others may write it
    /// (<see cref="RegularFiles.ReadOwned"/>).</summary>
    FileReadResult ReadStateFile(string path, int maxBytes);

    /// <summary>A file ANOTHER account controls, read by this process (plan §15q R1.1): the user configuration layer and the
    /// target user's own files read as root. Regular, nonblocking, capped, reached from <paramref name="beneath"/> (the home)
    /// through NO link (E7.S0 review S6, <see cref="BeneathFiles"/>), and on Linux only when the open descriptor's owner is
    /// <paramref name="owner"/> and neither group nor others may write it.</summary>
    FileReadResult ReadUserFile(string path, int maxBytes, uint owner, string beneath);

    /// <summary>A file whose owner is no evidence (plan §15q R1.1, review M2: the Windows profile through drvfs): regular,
    /// nonblocking, capped, reached from the folder that holds the drive letter's folder through NO link (E7.S0 review S1,
    /// <see cref="BeneathFiles.DriveBase"/>), with no uid or mode check.</summary>
    FileReadResult ReadNoFollowFile(string path, int maxBytes);

    bool FileExists(string path);

    bool DirectoryExists(string path);

    /// <summary>Full paths of the immediate subdirectories, in ordinal order; empty when <paramref name="path"/> does not
    /// exist. Never the disk's own order: an ext4 directory reads in the order of a hash seeded per filesystem, so the
    /// same tree answers a different order on every machine (and every answer built from a listing would follow it).</summary>
    IReadOnlyList<string> ListDirectories(string path);

    /// <summary>The target a link points at, read without following it (<c>readlink</c>).</summary>
    LinkReadResult ReadLink(string path);

    /// <summary>Size and free space of the filesystem that holds <paramref name="path"/> — one call, no walk.</summary>
    VolumeReadResult MeasureVolume(string path);

    /// <summary>The length and last write of one file — a stat, never a read (a container log can be gigabytes).</summary>
    FileSizeResult FileSize(string path);

    /// <summary>The last write of one directory, UTC — a stat, never a walk.</summary>
    DirectoryTimeResult DirectoryLastWrite(string path);

    /// <summary>Full paths of the files directly in <paramref name="path"/> (no directories, no recursion), in ordinal
    /// order for the reason <see cref="ListDirectories"/> gives; empty when it does not exist.</summary>
    IReadOnlyList<string> ListFiles(string path);

    /// <summary>A regular file in place of the symbolic link at <paramref name="path"/> — the link replaced, never what it points
    /// at (E7.S0 review C3: <c>config set</c> repairs a linked user layer root refuses). Judged where the link itself lives.</summary>
    DeletionVerdict ReplaceLinkWithFile(string path, ReadOnlySpan<byte> content, DeletionScope scope);

    /// <summary>
    /// The summed size of the files under <paramref name="path"/>, within <paramref name="limits"/>. Links are
    /// NEVER followed (a symlink or junction is neither counted nor entered), unreadable directories are skipped,
    /// file contents are never read — a stat per entry. When <paramref name="countOnlyUnder"/> is non-empty only
    /// files below a directory of one of those names count (<c>bin</c>, <c>obj</c>); directories named in
    /// <paramref name="neverEnter"/> are not walked at all (<c>node_modules</c>, <c>.git</c>).
    /// </summary>
    TreeMeasure MeasureTree(string path, TreeLimits limits, IReadOnlySet<string> countOnlyUnder, IReadOnlySet<string> neverEnter, CancellationToken cancellationToken);

    /// <summary>
    /// Whether this process may create a file in <paramref name="directory"/> (creating the directory first when
    /// it is missing): a temporary file is opened with delete-on-close and closed at once. Nothing remains —
    /// and for a process that may NOT write there, nothing was ever created.
    /// </summary>
    WriteAccess ProbeWriteAccess(string directory);

    /// <summary>
    /// Takes an exclusive lock file and holds it until the handle is disposed. The atomic operation it rests on
    /// is an exclusive open (<c>FileShare.None</c> — <c>flock</c> on Linux, a sharing violation on Windows); the
    /// operating system releases it when the holder dies, so a lock is never stale and nothing sweeps it.
    /// Residual: the lock file itself stays on disk (empty), which is harmless — holding is the open, not the file.
    /// </summary>
    ExclusiveLock TryLockExclusive(string lockPath);

    /// <summary>
    /// Rewrites a line file (<c>history.jsonl</c>) under the SAME lock <see cref="AppendLine"/> takes, so an
    /// append that arrives meanwhile waits and lands in the new file rather than in a file about to be replaced.
    /// <paramref name="keep"/> gets the current lines (none when the file is missing) and returns the lines to
    /// keep; when they are the same, nothing is written. The new content is written atomically, judged by
    /// <paramref name="scope"/>.
    /// </summary>
    DeletionVerdict RewriteLines(string path, Func<IReadOnlyList<string>, IReadOnlyList<string>> keep, DeletionScope scope, TimeSpan lockTimeout);

    void CreateDirectory(string path);

    /// <summary>
    /// Writes the whole file so that a reader sees either the old content or the new, never a
    /// half: the bytes go to a sibling temporary file first and replace the target in one rename.
    /// The target passes the deletion policy, because the rename overwrites it; so does the temporary
    /// file, which is made in the target's RESOLVED parent; and the target is resolved again just
    /// before the rename, so a link swapped in after the decision is refused, not followed.
    /// </summary>
    DeletionVerdict WriteFileAtomically(string path, ReadOnlySpan<byte> content, DeletionScope scope);

    /// <summary>
    /// Creates <paramref name="path"/> ONLY when it does not exist, with the whole content made visible in one step (plan
    /// §15k #1, #14: a request file): the bytes go to a sibling temporary file (0644) first and are then LINKED to the final
    /// name — <c>link(2)</c>, which fails on an existing name, on Linux; a non-replacing move on Windows — so a reader sees no
    /// file or the whole file, and two writers can never both win. A missing parent is created (0755 on Linux). Judged by the
    /// deletion policy like an atomic write.
    /// </summary>
    ExclusiveCreate CreateFileExclusively(string path, ReadOnlySpan<byte> content, DeletionScope scope);

    /// <summary>
    /// Appends one line so that two processes appending at once produce two whole lines.
    /// </summary>
    /// <remarks>
    /// The atomic operation it rests on is an EXCLUSIVE OPEN of a sibling lock file
    /// (<c>{path}.lock</c>, <c>FileShare.None</c> — <c>flock</c> on Linux, a sharing violation on
    /// Windows): the second writer waits, in short bounded retries, until the first has released the
    /// handle. The handle is released by the operating system when a holder dies, so there is no
    /// stale lock and nothing to sweep. <c>FileMode.Append</c> alone is NOT an atomic append across
    /// processes on Windows — it seeks to the end at open time — which is why the lock exists.
    /// Residual: a holder killed between the seek and the write leaves a torn last line; a reader of
    /// a JSONL file skips a line it cannot parse.
    /// </remarks>
    void AppendLine(string path, string line, TimeSpan lockTimeout);

    DeletionVerdict DeleteFile(string path, DeletionScope scope);

    DeletionVerdict DeleteDirectory(string path, DeletionScope scope);

    DeletionVerdict MoveFile(string from, string to, DeletionScope scope);

    DeletionVerdict MoveDirectory(string from, string to, DeletionScope scope);
}
