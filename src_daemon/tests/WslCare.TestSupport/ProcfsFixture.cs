using System.Globalization;

using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;

namespace WslCare.TestSupport;

/// <summary>
/// The procfs / cgroup tree captured from WSL Ubuntu on 2026-10-02 (<c>src_daemon/tests/fixtures/procfs/
/// ubuntu-2026-10-02</c>, copied beside every test project that links it; its <c>SOURCE.txt</c> says what
/// was captured, how, and what was redacted). The collectors read it as a sandboxed root.
/// </summary>
/// <remarks>The <c>cwd</c> links are in <c>links.txt</c>, not on disk: a Windows checkout cannot hold
/// symlinks. In-process tests read them through <see cref="LinkOverlay"/>; the scenario harness copies
/// the tree and makes real symlinks (<see cref="CopyTo"/>) where the OS allows it.</remarks>
public static class ProcfsFixture
{
    public const string Name = "ubuntu-2026-10-02";

    /// <summary>The fixture directory beside the test assembly; asserts it is there.</summary>
    public static string Root
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "procfs", Name);
            if (!Directory.Exists(path))
            {
                throw new DirectoryNotFoundException($"the procfs fixture should have been copied to {path}; does the project link ../fixtures?");
            }

            return path;
        }
    }

    /// <summary>When the tree was captured (<c>capture-info.txt</c>, line 1) — the "now" its ages are relative to.</summary>
    public static DateTimeOffset CapturedAt =>
        DateTimeOffset.Parse(File.ReadLines(Path.Combine(Root, "capture-info.txt")).First(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    /// <summary>Every recorded link: the path relative to the root (<c>proc/560/cwd</c>) → its target.</summary>
    public static IReadOnlyDictionary<string, string> Links =>
        File.ReadLines(Path.Combine(Root, "links.txt"))
            .Select(l => l.Split('\t', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);

    /// <summary>The Linux layout sandboxed at <paramref name="root"/> — what the collectors read.</summary>
    public static LinuxHostPaths PathsAt(string root) => new(LinuxEnvironment.Sandboxed(root));

    /// <summary>The fixture read in place, its links answered from <c>links.txt</c>.</summary>
    public static IFileSystem LinkOverlay(IFileSystem inner, string root) =>
        new LinkOverlayFileSystem(inner, Links.ToDictionary(l => Normalize($"{root}/{l.Key}"), l => l.Value, StringComparer.Ordinal));

    /// <summary>Copies the tree under <paramref name="root"/> and makes each recorded link a real symlink.
    /// Answers how many links could be made — none on an account that may not create symlinks.</summary>
    public static int CopyTo(string root)
    {
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(root, Path.GetRelativePath(Root, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return Links.Count(link => TryLink(Path.Combine(root, link.Key), link.Value));
    }

    private static bool TryLink(string path, string target)
    {
        try
        {
            File.CreateSymbolicLink(path, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    /// <summary>A real file system whose <see cref="IFileSystem.ReadLink"/> answers from the recorded table;
    /// a link the capture could not read is answered as unreadable, which is what it was.</summary>
    private sealed class LinkOverlayFileSystem(IFileSystem inner, IReadOnlyDictionary<string, string> links) : IFileSystem
    {
        public LinkReadResult ReadLink(string path) =>
            links.TryGetValue(Normalize(path), out var target)
                ? new LinkReadResult.Target(target)
                : new LinkReadResult.Unreadable("not recorded in the fixture (another user's process: readlink was refused at capture)");

        public FileReadResult ReadFile(string path) => inner.ReadFile(path);

        public bool FileExists(string path) => inner.FileExists(path);

        public bool DirectoryExists(string path) => inner.DirectoryExists(path);

        public IReadOnlyList<string> ListDirectories(string path) => inner.ListDirectories(path);

        public VolumeReadResult MeasureVolume(string path) => inner.MeasureVolume(path);

        public void CreateDirectory(string path) => inner.CreateDirectory(path);

        public DeletionVerdict WriteFileAtomically(string path, ReadOnlySpan<byte> content, DeletionScope scope) => inner.WriteFileAtomically(path, content, scope);

        public void AppendLine(string path, string line, TimeSpan lockTimeout) => inner.AppendLine(path, line, lockTimeout);

        public DeletionVerdict DeleteFile(string path, DeletionScope scope) => inner.DeleteFile(path, scope);

        public DeletionVerdict DeleteDirectory(string path, DeletionScope scope) => inner.DeleteDirectory(path, scope);

        public DeletionVerdict MoveFile(string from, string to, DeletionScope scope) => inner.MoveFile(from, to, scope);

        public DeletionVerdict MoveDirectory(string from, string to, DeletionScope scope) => inner.MoveDirectory(from, to, scope);
    }
}
