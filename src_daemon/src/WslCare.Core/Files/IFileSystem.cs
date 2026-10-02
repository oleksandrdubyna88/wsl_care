using WslCare.Core.Files.Deletion;

namespace WslCare.Core.Files;

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

    bool FileExists(string path);

    bool DirectoryExists(string path);

    /// <summary>Full paths of the immediate subdirectories; empty when <paramref name="path"/> does not exist.</summary>
    IReadOnlyList<string> ListDirectories(string path);

    void CreateDirectory(string path);

    /// <summary>
    /// Writes the whole file so that a reader sees either the old content or the new, never a
    /// half: the bytes go to a sibling temporary file first and replace the target in one rename.
    /// The target passes the deletion policy, because the rename overwrites it.
    /// </summary>
    DeletionVerdict WriteFileAtomically(string path, ReadOnlySpan<byte> content, DeletionScope scope);

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
