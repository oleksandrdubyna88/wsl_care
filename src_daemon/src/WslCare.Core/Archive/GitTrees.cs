using WslCare.Core.Files;
using WslCare.Core.Folders;

namespace WslCare.Core.Archive;

/// <summary>What phase 2's re-check found — a closed set.</summary>
public abstract record GitTreeFound
{
    private GitTreeFound()
    {
    }

    /// <summary>No <c>.git</c> entry touches the unit.</summary>
    public sealed record None : GitTreeFound;

    /// <summary>A <c>.git</c> entry touches it: <paramref name="Entry"/> names it.</summary>
    public sealed record Found(string Entry) : GitTreeFound;

    /// <summary>A companion folder could not be walked whole, so whether one does is not known.</summary>
    public sealed record Unchecked(string Why) : GitTreeFound;
}

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

    /// <summary>The <c>.git</c> entry in <paramref name="folder"/> or in any folder above it up to the file system's root — along the
    /// path as spelled AND along its real path (security review M-1: a link on the way may lead into a working tree the spelled path
    /// has nothing of); empty when none is — the folder is then inside no working tree.</summary>
    public static string Around(IFileSystem files, string folder) =>
        AroundSpelled(files, folder) is { Length: > 0 } spelled ? spelled : AroundReal(files, folder);

    private static string AroundSpelled(IFileSystem files, string folder)
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

    /// <summary>Phase 2 (owner rule 2026-10-07): a git working tree may have APPEARED since the copy. The unit is checked around its
    /// key's folder up to the root, in every folder on the way to its files, and below every folder only its companions live in —
    /// one bounded walk each (the walk's own limits, no link followed), whose files are checked the same way.</summary>
    public static GitTreeFound InUnit(IFileSystem files, string under, string key, IReadOnlyList<string> originals)
    {
        var keyFolder = Path.GetDirectoryName(Path.Combine(under, key)) ?? under;
        var around = Around(files, keyFolder);
        var found = around.Length > 0 ? around : originals.FirstOrDefault(NamesGit) ?? Between(files, under, originals);
        return found.Length > 0 ? new GitTreeFound.Found(found) : Below(files, under, Tops(ArchiveRemove.LeftFolders(key, originals)));
    }

    private static string AroundReal(IFileSystem files, string folder) =>
        files.ResolvePath(folder) is RealPathResult.Resolved real && !string.Equals(real.Path, folder, StringComparison.Ordinal) ? AroundSpelled(files, real.Path) : string.Empty;

    /// <summary>The outermost of the companion folders (a folder inside another is walked with it).</summary>
    private static IEnumerable<string> Tops(IReadOnlyList<string> folders) =>
        folders.Where(f => !folders.Any(g => f.StartsWith(g + "/", StringComparison.Ordinal)));

    private static GitTreeFound Below(IFileSystem files, string under, IEnumerable<string> tops) =>
        tops.Select(top => Walked(files, under, top)).FirstOrDefault(g => g is not GitTreeFound.None) ?? new GitTreeFound.None();

    private static readonly TreeRules WalkRules = new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal)) { ListFiles = true };

    private static GitTreeFound Walked(IFileSystem files, string under, string top) =>
        files.WalkTree(Path.Combine(under, top), FolderSizes.Limits, WalkRules, CancellationToken.None) switch
        {
            TreeMeasure.Measured { Complete: true } m => FoundIn(files, under, [.. m.Listed.Select(f => Path.GetRelativePath(under, f.Path).Replace('\\', '/'))]),
            TreeMeasure.Measured m => new GitTreeFound.Unchecked($"{top} could not be walked whole ({m.Note})"),
            TreeMeasure.Unreadable u => new GitTreeFound.Unchecked($"{top}: {u.Reason}"),
            _ => new GitTreeFound.None(),
        };

    private static GitTreeFound FoundIn(IFileSystem files, string under, IReadOnlyList<string> relatives) =>
        (relatives.FirstOrDefault(NamesGit) ?? Between(files, under, relatives)) is { Length: > 0 } found ? new GitTreeFound.Found(found) : new GitTreeFound.None();

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
