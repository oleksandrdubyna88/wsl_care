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
/// Every destructive method resolves the target (and, for a move, the destination) to its REAL path
/// through <see cref="RealPath"/>, asks the <see cref="DeletionPolicy"/>, and only then acts. The
/// protected roots are resolved once, at construction, for the reason given on
/// <see cref="ProtectedRoots"/>.
/// </remarks>
public sealed class PhysicalFileSystem : IFileSystem
{
    private readonly PathRules _rules = PathRules.ForThisOs;
    private readonly DeletionPolicy _policy;

    public PhysicalFileSystem(IHostPaths paths)
    {
        _policy = new DeletionPolicy(ProtectedRoots.From(paths, Real), _rules);
    }

    public FileReadResult ReadFile(string path)
    {
        try
        {
            return new FileReadResult.Content(File.ReadAllBytes(path));
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

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> ListDirectories(string path) =>
        Directory.Exists(path) ? Directory.GetDirectories(path) : [];

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public DeletionVerdict WriteFileAtomically(string path, ReadOnlySpan<byte> content, DeletionScope scope)
    {
        var verdict = Judge(FileOperation.Delete, path, string.Empty, scope);
        if (!verdict.IsAllowed)
        {
            return verdict;
        }

        // A sibling, so the rename stays on one volume; unique, so two writers never share a temp file.
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
        return verdict;
    }

    public void AppendLine(string path, string line, TimeSpan lockTimeout)
    {
        using var held = AcquireLock(path + ".lock", lockTimeout);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(Encoding.UTF8.GetBytes(line + "\n"));
        stream.Flush(flushToDisk: true);
    }

    public DeletionVerdict DeleteFile(string path, DeletionScope scope) =>
        Act(Judge(FileOperation.Delete, path, string.Empty, scope), () => File.Delete(path));

    public DeletionVerdict DeleteDirectory(string path, DeletionScope scope) =>
        Act(Judge(FileOperation.Delete, path, string.Empty, scope), () => Directory.Delete(path, recursive: true));

    public DeletionVerdict MoveFile(string from, string to, DeletionScope scope) =>
        Act(Judge(FileOperation.Move, from, to, scope), () => File.Move(from, to));

    public DeletionVerdict MoveDirectory(string from, string to, DeletionScope scope) =>
        Act(Judge(FileOperation.Move, from, to, scope), () => Directory.Move(from, to));

    private static DeletionVerdict Act(DeletionVerdict verdict, Action act)
    {
        if (verdict.IsAllowed)
        {
            act();
        }

        return verdict;
    }

    private DeletionVerdict Judge(FileOperation operation, string path, string destination, DeletionScope scope) =>
        _policy.Decide(new DeletionRequest(
            operation,
            Real(path),
            destination.Length == 0 ? string.Empty : Real(destination),
            Real(scope.Root),
            scope.Action,
            scope.Permit));

    /// <summary>The real path: absolute, every link followed, <c>..</c> applied to the real parent.</summary>
    private string Real(string path) => RealPath.Resolve(Path.GetFullPath(path), _rules, LinkTargetOf);

    private static string? LinkTargetOf(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A component we cannot inspect is not one we can follow; it is treated as a plain name.
            return null;
        }
    }

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
