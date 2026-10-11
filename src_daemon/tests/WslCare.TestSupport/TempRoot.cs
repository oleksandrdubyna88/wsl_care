namespace WslCare.TestSupport;

/// <summary>
/// A directory of this test's own under the system temp folder, removed on dispose. Everything a
/// test writes lives under it; nothing real is ever touched.
/// </summary>
/// <remarks>Deleting it here with <c>Directory.Delete</c> is fine: this is test support, not
/// <c>src_daemon/src</c>, so the architecture test does not reach it — and the deletion policy would
/// have nothing to say about a folder the test created a second ago.</remarks>
public sealed class TempRoot : IDisposable
{
    public TempRoot(string purpose)
    {
        _ = Swept.Value;
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wsl-care-test-{purpose}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    /// <summary>The absolute path of this test's root.</summary>
    public string Path { get; }

    /// <summary>How old a root left behind by an EARLIER test process must be before a later one removes it.</summary>
    public static readonly TimeSpan LeftoverAge = TimeSpan.FromHours(24);

    /// <summary>The exact shape the constructor writes: the prefix, a purpose, a dash and 32 hex digits.</summary>
    private static readonly System.Text.RegularExpressions.Regex RootName =
        new("^wsl-care-test-.+-[0-9a-f]{32}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The leftovers of earlier processes, removed once, by the first root this process makes.</summary>
    private static readonly Lazy<int> Swept = new(() => SweepLeftovers(System.IO.Path.GetTempPath(), DateTime.UtcNow, LeftoverAge));

    /// <summary>
    /// Removes the roots in <paramref name="tempFolder"/> that an earlier test process left behind — only this harness's exact
    /// names, only when last written before <paramref name="olderThan"/> ago — and answers how many went. A root it cannot remove
    /// (a handle still open) stays for a later sweep; nothing else in the folder is ever touched.
    /// </summary>
    public static int SweepLeftovers(string tempFolder, DateTime nowUtc, TimeSpan olderThan) =>
        new DirectoryInfo(tempFolder).EnumerateDirectories()
            .Where(d => RootName.IsMatch(d.Name) && nowUtc - d.LastWriteTimeUtc > olderThan)
            .Count(d => DeleteBestEffort(d.FullName));

    public string File(string relativePath, string content)
    {
        var full = Under(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public string Dir(string relativePath)
    {
        var full = Under(relativePath);
        Directory.CreateDirectory(full);
        return full;
    }

    public string Under(string relativePath) =>
        System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public void Dispose() => DeleteBestEffort(Path);

    /// <summary>
    /// Best effort, twice: on Windows a recursive delete over a tree holding a junction removes the
    /// junction and STILL throws <see cref="UnauthorizedAccessException"/> for it (measured 2026-10-02),
    /// so the second pass finishes what the first one started. A handle still open elsewhere leaves the
    /// folder to a later test process's <see cref="SweepLeftovers"/> — a test must never fail on its own cleanup.
    /// Answers whether the folder is gone.
    /// </summary>
    private static bool DeleteBestEffort(string path)
    {
        for (var attempt = 0; attempt < 2 && Directory.Exists(path); attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Retried once; then left to a later sweep.
            }
        }

        return !Directory.Exists(path);
    }
}
