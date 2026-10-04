using System.Text;

using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;

namespace WslCare.Core.Files;

/// <summary>
/// The disk, behind <see cref="IFileSystem"/>. The ONLY file under <c>src_daemon/src</c> that may
/// call <c>File.Delete</c>, <c>File.Move</c>, <c>Directory.Delete</c> or <c>Directory.Move</c> — the
/// architecture test holds every other file to that.
/// </summary>
/// <remarks>
/// <para>Every destructive method resolves the target (and, for a move, the destination, and always
/// the declared root) to its REAL path through <see cref="RealPath"/>, asks the
/// <see cref="DeletionPolicy"/>, and only then acts. A path whose real location cannot be established
/// — a component that cannot be inspected, a cycle of links — is refused by
/// <see cref="DeletionRule.Unresolvable"/>: fail closed. The protected roots are resolved once, at
/// construction, for the reason given on <see cref="ProtectedRoots"/>.</para>
/// <para>The atomic write also ACTS on the real paths it judged and checks them again just before the
/// rename; see <see cref="WriteFileAtomically"/> for the window that remains.</para>
/// </remarks>
public sealed class PhysicalFileSystem : IFileSystem
{
    private static readonly RealPathResult NoDestination = new RealPathResult.Resolved(string.Empty);

    private readonly PathRules _rules = PathRules.ForThisOs;
    private readonly DeletionPolicy _policy;
    private readonly Func<string, string?> _readLinkTarget;
    private readonly Action<AtomicWriteStep, string> _onAtomicWriteStep;

    public PhysicalFileSystem(IHostPaths paths)
        : this(paths, ReadLinkTarget, static (_, _) => { })
    {
    }

    /// <summary>The seams, for tests only: <paramref name="readLinkTarget"/> stands in for the disk's
    /// answer about one component (it may throw, as the real one does for a component it cannot
    /// inspect), and <paramref name="onAtomicWriteStep"/> runs between the steps of
    /// <see cref="WriteFileAtomically"/>, so a test can swap a link in at the exact moment a race would.</summary>
    internal PhysicalFileSystem(IHostPaths paths, Func<string, string?> readLinkTarget, Action<AtomicWriteStep, string> onAtomicWriteStep)
    {
        _readLinkTarget = readLinkTarget;
        _onAtomicWriteStep = onAtomicWriteStep;
        _policy = new DeletionPolicy(ProtectedRoots.From(paths, RealOrSpelled), _rules);
    }

    public FileReadResult ReadFile(string path)
    {
        try
        {
            using var stream = OpenForReading(path);
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            return new FileReadResult.Content(bytes.ToArray());
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new FileReadResult.Missing();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new FileReadResult.Unreadable(e.Message);
        }
    }

    public FileReadResult ReadRegularFile(string path, int maxBytes) => RegularFiles.Read(path, maxBytes);

    /// <summary>The uid a state file must be owned by on Linux (<see cref="ReadStateFile"/>): root — unless a sandbox (a test,
    /// <c>WSL_CARE_ROOT</c>) says the state there is its own process's.</summary>
    public uint TrustedStateOwner { get; init; }

    public FileReadResult ReadStateFile(string path, int maxBytes) => RegularFiles.ReadOwned(path, maxBytes, TrustedStateOwner);

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> ListDirectories(string path) =>
        Directory.Exists(path) ? Ordinal(Directory.GetDirectories(path)) : [];

    /// <summary>A listing in ordinal order — the disk's own order is the filesystem's (see <see cref="IFileSystem.ListDirectories"/>).</summary>
    private static string[] Ordinal(string[] paths)
    {
        Array.Sort(paths, StringComparer.Ordinal);
        return paths;
    }

    /// <summary>Through the same reader the deletion policy trusts (attributes first, then the target),
    /// so "cannot be inspected" is an answer here too rather than a silent "not a link".</summary>
    public LinkReadResult ReadLink(string path)
    {
        try
        {
            return _readLinkTarget(path) is { } target ? new LinkReadResult.Target(target) : new LinkReadResult.NotALink();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new LinkReadResult.Unreadable(e.Message);
        }
    }

    public VolumeReadResult MeasureVolume(string path)
    {
        try
        {
            var drive = new DriveInfo(path);
            return new VolumeReadResult.Measured(drive.TotalSize, drive.TotalFreeSpace, drive.AvailableFreeSpace);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new VolumeReadResult.Unreadable(e.Message);
        }
    }

    public FileSizeResult FileSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileSizeResult.Measured(info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)) : new FileSizeResult.Missing();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new FileSizeResult.Unreadable(e.Message);
        }
    }

    public DirectoryTimeResult DirectoryLastWrite(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists ? new DirectoryTimeResult.Measured(new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)) : new DirectoryTimeResult.Missing();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new DirectoryTimeResult.Unreadable(e.Message);
        }
    }

    public IReadOnlyList<string> ListFiles(string path) =>
        Directory.Exists(path) ? Ordinal(Directory.GetFiles(path)) : [];

    public TreeMeasure MeasureTree(string path, TreeLimits limits, IReadOnlySet<string> countOnlyUnder, IReadOnlySet<string> neverEnter, CancellationToken cancellationToken) =>
        TreeWalk.Measure(path, limits, countOnlyUnder, neverEnter, cancellationToken);

    public WriteAccess ProbeWriteAccess(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            using var probe = new FileStream(
                Path.Combine(directory, $".wsl-care-write-probe-{Guid.NewGuid():N}"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            return new WriteAccess.Writable();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new WriteAccess.NotWritable($"{directory} is not writable by this process ({e.Message})");
        }
    }

    public ExclusiveLock TryLockExclusive(string lockPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath) ?? lockPath);
            return new ExclusiveLock.Held(new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException e)
        {
            return new ExclusiveLock.Busy($"{lockPath} is held by another process ({e.Message})");
        }
    }

    public DeletionVerdict RewriteLines(string path, Func<IReadOnlyList<string>, IReadOnlyList<string>> keep, DeletionScope scope, TimeSpan lockTimeout)
    {
        using var held = AcquireLock(path + ".lock", lockTimeout);
        IReadOnlyList<string> current = File.Exists(path)
            ? [.. File.ReadAllText(path, Encoding.UTF8).Split('\n').Where(l => l.Length > 0)]
            : [];
        var kept = keep(current);
        if (kept.SequenceEqual(current, StringComparer.Ordinal))
        {
            return DeletionVerdict.Allowed;
        }

        var text = kept.Count == 0 ? string.Empty : string.Join('\n', kept) + "\n";
        return WriteFileAtomically(path, Encoding.UTF8.GetBytes(text), scope);
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    /// <summary>
    /// Judge the target, build the temporary file inside the target's RESOLVED parent and judge it too,
    /// write it, resolve the target and its parent again, and rename — on the resolved paths, never on
    /// the spelling the caller passed.
    /// </summary>
    /// <remarks>
    /// <para>What this closes: a link swapped in after the target was approved (the temporary file would
    /// be created through it) and one swapped in after the temporary file was written (the rename would
    /// follow it). Either is refused — by the policy when the new place is outside the scope or
    /// protected, by <see cref="DeletionRule.PathChanged"/> when it is merely not the approved place —
    /// and the temporary file is removed through the policy like any delete (left, and named, when the
    /// swap carried it somewhere the scope may not reach).</para>
    /// <para>What remains — the residual window: between the last check and the rename itself a
    /// component can still be swapped, because the rename takes a path and the kernel resolves it again.
    /// Closing that needs a rename relative to a directory handle held open since the check
    /// (<c>renameat</c> / <c>SetFileInformationByHandle</c>), which .NET does not expose and this code
    /// does not P/Invoke. Exploiting that window needs write access to the directory being written — for
    /// the one caller today, the user's own config directory — which means being the same user: outside
    /// the threat model (the policy guards against this product's own mistakes and against links planted
    /// where a cleanup walks, not against the account it runs as).</para>
    /// </remarks>
    public DeletionVerdict WriteFileAtomically(string path, ReadOnlySpan<byte> content, DeletionScope scope)
    {
        var target = Judge(FileOperation.Delete, path, string.Empty, scope);
        if (!target.Verdict.IsAllowed)
        {
            return target.Verdict;
        }

        _onAtomicWriteStep(AtomicWriteStep.TargetApproved, target.RealPath);
        var temp = JudgeTemp(target, scope);
        if (!temp.Verdict.IsAllowed)
        {
            return temp.Verdict;
        }

        WriteNew(temp.RealPath, content);
        _onAtomicWriteStep(AtomicWriteStep.TempWritten, temp.RealPath);
        var recheck = Revalidate(path, target, scope);
        if (!recheck.IsAllowed)
        {
            return Abandon(temp.RealPath, scope, recheck);
        }

        MoveReplacing(temp.RealPath, target.RealPath);
        return target.Verdict;
    }

    public ExclusiveCreate CreateFileExclusively(string path, ReadOnlySpan<byte> content, DeletionScope scope)
    {
        EnsureParent(path);
        var target = Judge(FileOperation.Delete, path, string.Empty, scope);
        var temp = target.Verdict.IsAllowed ? JudgeTemp(target, scope) : target;
        if (!temp.Verdict.IsAllowed)
        {
            return new ExclusiveCreate.Refused(Reason(temp.Verdict));
        }

        WriteNew(temp.RealPath, content);
        RegularFiles.MakeReadable(temp.RealPath);
        var recheck = Revalidate(path, target, scope);
        if (!recheck.IsAllowed)
        {
            return new ExclusiveCreate.Refused(Reason(Abandon(temp.RealPath, scope, recheck)));
        }

        var created = LinkNew(temp.RealPath, target.RealPath);
        DeleteFile(temp.RealPath, scope);
        return created ? new ExclusiveCreate.Created() : new ExclusiveCreate.AlreadyExists();
    }

    /// <summary>A second name <paramref name="to"/> for <paramref name="from"/> only when <paramref name="to"/> does not exist —
    /// atomically: <c>link(2)</c> on Linux, a non-replacing move on Windows. False when the name existed.</summary>
    private static bool LinkNew(string from, string to)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                File.Move(from, to, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(to))
            {
                return false;
            }
        }

        return RegularFiles.LinkErrno(from, to) switch
        {
            0 => true,
            RegularFiles.NameExists => false,
            var errno => throw new IOException($"link {from} -> {to} failed (errno {errno})"),
        };
    }

    /// <summary>The parent of an exclusively created file: made when missing — 0755 on Linux, whatever the umask.</summary>
    private static void EnsureParent(string path)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (parent is null || Directory.Exists(parent))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(parent);
            return;
        }

        // mkdir(2) masks the mode with the umask (a root shell with umask 077 made it 0700, and the unprivileged status could no
        // longer see the queue — E6.S1, DetachFlows): the mode is SET after the folder exists.
        const UnixFileMode Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        Directory.CreateDirectory(parent, Mode);
        File.SetUnixFileMode(parent, Mode);
    }

    private static string Reason(DeletionVerdict verdict) => verdict is DeletionVerdict.Refused refused ? refused.Reason : "refused by the deletion policy";

    /// <summary>
    /// The atomic write's rename over the target. On Windows a rename onto a file another handle holds open is refused
    /// (access denied) even when that handle shares delete — a reader of <c>running.json</c> or the history holds it for
    /// microseconds — so the rename is retried for up to <see cref="ReplaceRetryFor"/> before the failure is thrown. On Linux
    /// a rename never meets an open file, and a failure is real at once.
    /// </summary>
    private static void MoveReplacing(string from, string to)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                File.Move(from, to, overwrite: true);
                return;
            }
            catch (Exception e) when (IsHeldOpenOnWindows(e) && started.Elapsed < ReplaceRetryFor)
            {
                Thread.Sleep(10);
            }
        }
    }

    /// <summary>The refusal Windows gives a rename onto a file a reader holds open — an access or I/O error that is not a
    /// missing file or folder.</summary>
    private static bool IsHeldOpenOnWindows(Exception e) =>
        OperatingSystem.IsWindows() && e is UnauthorizedAccessException or IOException && e is not (FileNotFoundException or DirectoryNotFoundException);

    /// <summary>How long the atomic write's rename waits out a reader on Windows.</summary>
    internal static readonly TimeSpan ReplaceRetryFor = TimeSpan.FromSeconds(2);

    public void AppendLine(string path, string line, TimeSpan lockTimeout)
    {
        using var held = AcquireLock(path + ".lock", lockTimeout);
        var torn = EndsTorn(path);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(Encoding.UTF8.GetBytes((torn ? "\n" : string.Empty) + line + "\n"));
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Whether the file's last byte is not a newline — the torn remains of a writer that died mid-append, which the
    /// next line must not continue (gate finding #6: it would swallow a whole record).</summary>
    private static bool EndsTorn(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        using var stream = OpenForReading(path);
        if (stream.Length == 0)
        {
            return false;
        }

        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() != '\n';
    }

    public DeletionVerdict DeleteFile(string path, DeletionScope scope) =>
        Act(Judge(FileOperation.Delete, path, string.Empty, scope).Verdict, () => File.Delete(path));

    public DeletionVerdict DeleteDirectory(string path, DeletionScope scope) =>
        Act(Judge(FileOperation.Delete, path, string.Empty, scope).Verdict, () => Directory.Delete(path, recursive: true));

    public DeletionVerdict MoveFile(string from, string to, DeletionScope scope) =>
        Act(Judge(FileOperation.Move, from, to, scope).Verdict, () => File.Move(from, to));

    public DeletionVerdict MoveDirectory(string from, string to, DeletionScope scope) =>
        Act(Judge(FileOperation.Move, from, to, scope).Verdict, () => Directory.Move(from, to));

    /// <summary>
    /// The real reader behind the seam: the link target of one component, <c>null</c> when it is not a
    /// link or does not exist, and an exception when it cannot be inspected.
    /// </summary>
    /// <remarks>Measured 2026-10-02 on Windows 11 and WSL Ubuntu (.NET 10), inside a directory this
    /// account was denied: <c>FileInfo.LinkTarget</c> answers <c>null</c> — it never throws, on either
    /// family — while <c>FileInfo.Attributes</c> throws <see cref="UnauthorizedAccessException"/>. So the
    /// attributes are read first: they throw for a component that cannot be inspected, answer <c>-1</c>
    /// for one that does not exist, and carry <c>ReparsePoint</c> for a symlink or a junction. A reparse
    /// point whose target cannot be read (a cloud placeholder, a broken reparse buffer) is not vouched
    /// for as a plain name either.</remarks>
    internal static string? ReadLinkTarget(string path)
    {
        var info = new FileInfo(path);
        var attributes = info.Attributes;
        if ((int)attributes == -1 || !attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return null;
        }

        return info.LinkTarget ?? throw new IOException($"{path} is a reparse point whose link target cannot be read");
    }

    private static DeletionVerdict Act(DeletionVerdict verdict, Action act)
    {
        if (verdict.IsAllowed)
        {
            act();
        }

        return verdict;
    }

    /// <summary>A decision and the real paths it was made on — the paths an atomic write then acts on.</summary>
    private sealed record Judged(DeletionVerdict Verdict, string RealPath, string RealDestination);

    private Judged Judge(FileOperation operation, string path, string destination, DeletionScope scope)
    {
        var target = Real(path);
        var moveTo = destination.Length == 0 ? NoDestination : Real(destination);
        var root = Real(scope.Root);
        if (FirstFailure(target, moveTo, root) is { } failure)
        {
            return new Judged(DeletionPolicy.Unresolvable(operation, scope.Action, path, failure.Component, failure.Reason), string.Empty, string.Empty);
        }

        var request = new DeletionRequest(operation, PathOf(target), PathOf(moveTo), PathOf(root), scope.Action, scope.Permit);
        return new Judged(_policy.Decide(request), request.RealPath, request.RealDestination);
    }

    private static RealPathResult.Unresolvable? FirstFailure(params RealPathResult[] results) =>
        results.OfType<RealPathResult.Unresolvable>().FirstOrDefault();

    private static string PathOf(RealPathResult result) => ((RealPathResult.Resolved)result).Path;

    /// <summary>The temporary file: a sibling of the REAL target, so it is created inside the approved
    /// parent (and the rename stays on one volume); unique, so two writers never share one; judged by
    /// the same scope before it exists, and refused if that parent no longer resolves to itself.</summary>
    private Judged JudgeTemp(Judged target, DeletionScope scope)
    {
        var temp = $"{target.RealPath}.{Guid.NewGuid():N}.tmp";
        var judged = Judge(FileOperation.Delete, temp, string.Empty, scope);
        return !judged.Verdict.IsAllowed || _rules.PathEquals(judged.RealPath, temp)
            ? judged
            : judged with { Verdict = DeletionPolicy.Changed(FileOperation.Delete, scope.Action, temp, judged.RealPath) };
    }

    private static void WriteNew(string path, ReadOnlySpan<byte> content)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Immediately before the rename: the caller's path must still resolve to the approved real
    /// target, that target's parent to itself, and the policy must still allow it. (A real path holds no
    /// link, so "the target is now a link" shows up as resolving somewhere else.)</summary>
    private DeletionVerdict Revalidate(string path, Judged approved, DeletionScope scope)
    {
        var again = Judge(FileOperation.Delete, path, string.Empty, scope);
        if (!again.Verdict.IsAllowed)
        {
            return again.Verdict;
        }

        var parent = Path.GetDirectoryName(approved.RealPath) ?? approved.RealPath;
        var sameParent = Real(parent) is RealPathResult.Resolved resolved && _rules.PathEquals(resolved.Path, parent);
        return sameParent && _rules.PathEquals(again.RealPath, approved.RealPath)
            ? DeletionVerdict.Allowed
            : DeletionPolicy.Changed(FileOperation.Delete, scope.Action, path, again.RealPath);
    }

    /// <summary>The re-check refused: the temporary file is removed through the policy like any delete,
    /// or left — and named in the refusal — when the swap carried it where the scope may not reach.</summary>
    private DeletionVerdict Abandon(string temp, DeletionScope scope, DeletionVerdict refusal)
    {
        var removal = DeleteFile(temp, scope);
        return removal.IsAllowed || refusal is not DeletionVerdict.Refused refused
            ? refusal
            : DeletionVerdict.Refuse(refused.Rule, $"{refused.Reason}; the temporary file {temp} was left where it now is, because removing it was refused too");
    }

    /// <summary>The real path: absolute, every link followed, <c>..</c> applied to the real parent — or why not.</summary>
    private RealPathResult Real(string path) => RealPath.Resolve(Path.GetFullPath(path), _rules, Inspect);

    /// <summary>For <see cref="ProtectedRoots"/>: a protected root whose real path cannot be established
    /// is kept as spelled. Still fail closed — a target spelled under it is under it, and one that
    /// reaches it through a link passes the same uninspectable component and is refused as
    /// unresolvable.</summary>
    private string RealOrSpelled(string path) =>
        Real(path) is RealPathResult.Resolved resolved ? resolved.Path : Path.GetFullPath(path);

    /// <summary>One component, asked of the disk through the seam. An inspection that throws is a typed
    /// failure — never a plain name.</summary>
    private LinkInspection Inspect(string path)
    {
        try
        {
            return _readLinkTarget(path) is { } target ? new LinkInspection.Link(target) : LinkInspection.NotALink;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new LinkInspection.Uninspectable(e.Message);
        }
    }

    /// <summary>
    /// How <see cref="ReadFile"/> opens a file: sharing read, WRITE and DELETE, so a reader never blocks a writer. On
    /// Windows a reader that shares only read (what <c>File.ReadAllBytes</c> does) makes a concurrent
    /// <see cref="AppendLine"/> fail with a sharing violation — a run's history line lost — and the atomic replace of
    /// <see cref="WriteFileAtomically"/> fail too. Found as the flaky <c>RunRetentionTests</c> race under load
    /// (2026-10-03): retention reads the history right after its rewrite releases the lock, while a racing run appends.
    /// A line torn by a concurrent append is already read as unparseable (<c>RunHistory</c>).
    /// </summary>
    internal static FileStream OpenForReading(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>The exclusive open described on <see cref="IFileSystem.AppendLine"/>, retried until <paramref name="timeout"/>.</summary>
    private static FileStream AcquireLock(string lockPath, TimeSpan timeout)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (started.Elapsed < timeout)
            {
                Thread.Sleep(Random.Shared.Next(5, 25));
            }
            catch (IOException e)
            {
                throw new TimeoutException($"could not take {lockPath} within {timeout.TotalSeconds:0.#} s", e);
            }
        }
    }
}

/// <summary>The points inside <see cref="PhysicalFileSystem.WriteFileAtomically"/> a test can stop at.</summary>
internal enum AtomicWriteStep
{
    /// <summary>The target was judged and allowed; nothing is written yet. The path is the real target.</summary>
    TargetApproved,

    /// <summary>The temporary file holds the new bytes; the final rename has not happened. The path is the temporary file.</summary>
    TempWritten,
}
