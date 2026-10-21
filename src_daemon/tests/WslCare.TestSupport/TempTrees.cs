namespace WslCare.TestSupport;

/// <summary>
/// Scratch trees in a SANDBOX temp folder for the tests of the Windows <c>%TEMP%</c> cleanup (A23): a folder with files, every time
/// stamped — files, sub-folders and the folder — by write time AND creation time, so a tree made a second ago is not "old" by its
/// creation time. Never pointed at a real temp folder: the caller hands the sandbox's.
/// </summary>
public static class TempTrees
{
    /// <summary>Makes <paramref name="relative"/> under <paramref name="temp"/> with the given files (name, days old) and stamps every
    /// folder and file; answers the folder's path.</summary>
    public static string Make(string temp, string relative, int daysOld, params (string File, int Days)[] files)
    {
        var folder = Path.Combine(temp, relative);
        Directory.CreateDirectory(folder);
        foreach (var (file, days) in files)
        {
            var path = Path.Combine(folder, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "scratch");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-days));
            File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-days));
        }

        foreach (var dir in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).Append(folder))
        {
            Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddDays(-daysOld));
            Directory.SetCreationTimeUtc(dir, DateTime.UtcNow.AddDays(-daysOld));
        }

        return folder;
    }
}
