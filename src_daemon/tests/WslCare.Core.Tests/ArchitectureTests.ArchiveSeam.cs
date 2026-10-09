using FluentAssertions;

namespace WslCare.Core.Tests;

/// <summary>
/// Plan §15r E9.S2a, review M12 — the archive's seam: a copy, a replace, a delete-on-close, a delete disposition and the native
/// rename / unlink / link / rmdir calls live ONLY in the file-system seam's files. Each pattern has a planted companion, so a
/// scanner that stopped matching cannot pass forever.
/// </summary>
public sealed partial class ArchitectureTests
{
    /// <summary>The file-system seam's files: the one that deletes and moves, its archive halves (Linux and shared, Windows), and the two holding the natives.</summary>
    private static IReadOnlyList<string> FileSystemSeamFiles()
    {
        var seam = Metadata("WslCare.FileSystemSeam");
        var folder = Path.GetDirectoryName(seam)!;
        return [seam, Path.Combine(folder, "PhysicalFileSystem.Archive.cs"), Path.Combine(folder, "PhysicalFileSystem.Archive.Windows.cs"), Path.Combine(folder, "PhysicalFileSystem.Archive.Records.cs"), Path.Combine(folder, "BeneathWrites.cs"), Path.Combine(folder, "RegularFiles.cs")];
    }

    [Fact]
    public void The_architecture_scan_finds_a_planted_copy_replace_delete_on_close_and_rename()
    {
        const string planted = """
            File.Copy(a, b);
            File
              .Replace(a, b, c);
            new FileInfo(a).CopyTo(b);
            var info = new FileInfo(a);
            info.Replace(b, c);
            using var s = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.None, 4096, FileOptions.DeleteOnClose);
            SetFileInformationByHandle(h, FileDispositionInfoEx, ref flags, 4);
            [LibraryImport("libc", EntryPoint = "renameat2")]
            [LibraryImport("libc", EntryPoint = "unlinkat")]
            [LibraryImport("libc", EntryPoint = "linkat")]
            [LibraryImport("libc", EntryPoint = "rmdir")]
            [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW")]
            [DllImport("libc")] static extern int rename(string a, string b);
            [LibraryImport("libc")] private static partial int unlinkat(int d, string p, int f);
            [DllImport("kernel32.dll")] public static extern bool MoveFileExW(string a, string b, uint f);
            """;

        // Own review round M3: the default idiom — the function's own name, no EntryPoint — is found too.
        ArchiveSeamScan(planted).Select(h => h.Line).Distinct().Should().Equal(1, 2, 4, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16);
    }

    [Fact]
    public void The_archive_seam_scanner_ignores_a_string_replace_and_a_stream_copy()
    {
        ArchiveSeamScan("""var x = s.Replace("a", "b"); stream.CopyTo(other); text.Replace('/', '\\'); var renamed = "rename";""").Should().BeEmpty();
    }

    [Fact]
    public void No_file_outside_the_seam_copies_replaces_deletes_on_close_or_renames()
    {
        var seam = FileSystemSeamFiles();
        var offenders = SourceFiles()
            .Where(file => !seam.Contains(file, StringComparer.OrdinalIgnoreCase))
            .SelectMany(file => ArchiveSeamScan(File.ReadAllText(file)).Select(hit => $"{file}:{hit.Line}: {hit.Text}"))
            .ToList();

        offenders.Should().BeEmpty("every copy, replace, delete disposition and native rename / unlink goes through the archive's seam (plan §15r E9.S2a)");
    }

    [Fact]
    public void The_archive_seam_scanner_still_finds_the_seams_own_calls()
    {
        FileSystemSeamFiles().Where(File.Exists).SelectMany(f => ArchiveSeamScan(File.ReadAllText(f))).Select(h => h.Text)
            .Should().Contain(t => t.Contains("renameat2", StringComparison.Ordinal)).And.Contain(t => t.Contains("unlinkat", StringComparison.Ordinal));
    }

    /// <summary>A copy or a replace by <c>File</c>, a delete-on-close, a delete disposition, and the native rename / unlink / link /
    /// rmdir / move entry points — written across whitespace, as the first scan is.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(
        @"\bFile\s*\.\s*(?:Copy|Replace)\s*\(|\bFileOptions\s*\.\s*DeleteOnClose\b|\bSetFileInformationByHandle\b|\bFileDisposition\w*|EntryPoint\s*=\s*""(?:rename|renameat2?|unlink|unlinkat|link|linkat|rmdir|MoveFileExW?|MoveFileW?|DeleteFileW?|RemoveDirectoryW?|CopyFileW?|CopyFile2|ReplaceFileW?)""|\b(?:extern|partial)\s+[\w.]+\s+(?:rename|renameat2?|unlink|unlinkat|link|linkat|rmdir|MoveFileExW?|MoveFileW?|DeleteFileW?|RemoveDirectoryW?|CopyFileW?|CopyFile2|ReplaceFileW?)\s*\(",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ArchiveForbiddenCall();

    /// <summary>A <c>FileInfo</c>'s <c>CopyTo</c> / <c>Replace</c>: chained on <c>new FileInfo(…)</c>, or on a name declared as one.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\bnew\s+FileInfo\s*\([^;]*?\)\s*\.\s*(?:CopyTo|Replace)\s*\(", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ChainedFileInfoCopy();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(?:FileInfo|var)\s+(\w+)\s*=\s*new\s+FileInfo\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex FileInfoName();

    /// <summary>Every match of the archive seam's patterns in <paramref name="source"/>: the 1-based line and the text matched.</summary>
    internal static IReadOnlyList<(int Line, string Text)> ArchiveSeamScan(string source)
    {
        var named = FileInfoName().Matches(source).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)
            .SelectMany(name => System.Text.RegularExpressions.Regex.Matches(source, $@"\b{name}\s*\.\s*(?:CopyTo|Replace)\s*\(", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
        return [.. ArchiveForbiddenCall().Matches(source).Concat(ChainedFileInfoCopy().Matches(source)).Concat(named)
            .OrderBy(m => m.Index).Select(m => (LineOf(source, m.Index), m.Value))];
    }
}
