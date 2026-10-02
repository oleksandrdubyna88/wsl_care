using System.Text;

using WslCare.Core.Files;

namespace WslCare.Core.Collectors.Procfs;

/// <summary>
/// Reading one pseudo-file through <see cref="IFileSystem"/> — the procfs root, the cgroup root and
/// the user database are paths a test points at a captured fixture tree, so every read goes through
/// the seam rather than <see cref="File"/>.
/// </summary>
public static class ProcText
{
    /// <summary>The file as UTF-8 text, or the reason it could not be read, naming the path.</summary>
    public static Reading<string> Read(IFileSystem files, string path) => Bytes(files, path).Map(Encoding.UTF8.GetString);

    /// <summary>The file's bytes (<c>cmdline</c> and <c>auxv</c> are binary), or the reason.</summary>
    public static Reading<byte[]> Bytes(IFileSystem files, string path) => files.ReadFile(path) switch
    {
        FileReadResult.Content content => Reading.Of(content.Bytes),
        FileReadResult.Missing => Reading.Missing<byte[]>($"{path} does not exist"),
        FileReadResult.Unreadable unreadable => Reading.Missing<byte[]>($"{path} could not be read: {unreadable.Reason}"),
        _ => throw new System.Diagnostics.UnreachableException("FileReadResult is a closed set"),
    };

    /// <summary>The last segment of a listed path. <see cref="IFileSystem.ListDirectories"/> answers in the
    /// host's separator, and a fixture tree read on Windows mixes both, so both count here; a procfs
    /// directory name never holds either.</summary>
    public static string LastSegment(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        return trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..];
    }

    /// <summary>The non-empty lines of a text, whatever its line ends.</summary>
    public static IEnumerable<string> Lines(string text) => text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0);
}
