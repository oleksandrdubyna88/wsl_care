namespace WslCare.TestSupport;

/// <summary>
/// A folder of this test's own under the system temp folder, named <c>testsToDelete-wsl-care-&lt;purpose&gt;-&lt;guid&gt;</c> as the shared
/// test-leftovers rule asks of everything a test creates OUTSIDE its process, and removed on dispose. The prefix is on the folder's OWN
/// name, so the family's sweep finds it should a crashed run leave it behind.
/// </summary>
/// <remarks>The worktree report is about folders and links, so its tests lay real trees out here. <see cref="TempRoot"/> keeps its older
/// name until the repository's own test folders take the prefix (PLAN_disk_hygiene_windows.md DH-8).</remarks>
public sealed class ToDeleteRoot : IDisposable
{
    public const string Prefix = "testsToDelete-";

    public ToDeleteRoot(string purpose)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Prefix}wsl-care-{purpose}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    /// <summary>The absolute path of the root.</summary>
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

    /// <summary>Removes the root — twice, because on Windows a recursive delete over a tree holding a junction removes the junction and
    /// STILL throws for it (measured 2026-10-02); a handle still open elsewhere leaves the folder to the family's sweep, and a test
    /// never fails on its own cleanup.</summary>
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
                // Retried once; then left to the sweep.
            }
        }
    }
}
