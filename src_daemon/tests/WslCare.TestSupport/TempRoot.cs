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
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wsl-care-test-{purpose}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    /// <summary>The absolute path of this test's root.</summary>
    public string Path { get; }

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

    /// <summary>
    /// Best effort, twice: on Windows a recursive delete over a tree holding a junction removes the
    /// junction and STILL throws <see cref="UnauthorizedAccessException"/> for it (measured 2026-10-02),
    /// so the second pass finishes what the first one started. A handle still open elsewhere leaves the
    /// folder to the operating system's temp sweep — a test must never fail on its own cleanup.
    /// </summary>
    public void Dispose()
    {
        for (var attempt = 0; attempt < 2 && Directory.Exists(Path); attempt++)
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Retried once; then left to the OS.
            }
        }
    }
}
