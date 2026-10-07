using WslCare.Core.Files;

namespace WslCare.Core.Archive;

/// <summary>
/// Owner rule 2026-10-07 — the archive never selects anything inside a git working tree or a <c>.git</c> folder, an original clone and a
/// worktree alike. A working tree is a folder holding a <c>.git</c> entry of any kind: a folder (a clone), a FILE (a worktree's or a
/// submodule's <c>gitdir:</c> pointer) or a link. Only names and stats are read; nothing is opened.
/// </summary>
public static class GitTrees
{
    /// <summary>The name git keeps its repository under; matched without case (NTFS folds it, and a near miss is never worth the risk).</summary>
    public const string Marker = ".git";

    /// <summary>Whether a relative path (<c>/</c>-separated) passes through a <c>.git</c> entry — the repository itself.</summary>
    public static bool NamesGit(string relative) =>
        relative.Split('/').Any(segment => string.Equals(segment, Marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>The <c>.git</c> entry in <paramref name="folder"/> or in any folder above it up to the file system's root; empty when
    /// none is — the folder is then inside no working tree.</summary>
    public static string Around(IFileSystem files, string folder)
    {
        for (var at = folder; !string.IsNullOrEmpty(at); at = Path.GetDirectoryName(at))
        {
            if (EntryIn(files, at) is { Length: > 0 } found)
            {
                return found;
            }
        }

        return string.Empty;
    }

    /// <summary>The first folder strictly below <paramref name="under"/> on the way to any of <paramref name="relatives"/> that holds a
    /// <c>.git</c> entry; empty when none does.</summary>
    public static string Between(IFileSystem files, string under, IEnumerable<string> relatives) =>
        relatives.SelectMany(Folders).Distinct(StringComparer.Ordinal)
            .Select(folder => EntryIn(files, Path.Combine(under, folder)))
            .FirstOrDefault(found => found.Length > 0) ?? string.Empty;

    /// <summary>Every folder of a relative file path, outermost first (<c>a/b/c.jsonl</c> → <c>a</c>, <c>a/b</c>).</summary>
    private static IEnumerable<string> Folders(string relative)
    {
        var segments = relative.Split('/');
        return Enumerable.Range(1, segments.Length - 1).Select(n => string.Join('/', segments.Take(n)));
    }

    private static string EntryIn(IFileSystem files, string folder)
    {
        var entry = Path.Combine(folder, Marker);
        return files.DirectoryExists(entry) || files.FileExists(entry) || files.ReadLink(entry) is LinkReadResult.Target ? entry : string.Empty;
    }
}
