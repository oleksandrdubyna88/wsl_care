using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Files;

namespace WslCare.Core.Processes;

/// <summary>
/// The ONE check that a path is root's alone — a directory (never a link) owned by uid 0 and writable by neither its group nor
/// others — shared by the Windows system drive's mount point (<see cref="SystemDriveFiles"/>) and the product's own binary
/// (<see cref="SelfBinary"/>, plan §15r D1): a folder somebody else may write lets them swap what is under it between a check
/// and a start. Pure over a status lookup, so every answer is a unit test; the product passes
/// <see cref="RegularFiles.StatNoFollow"/>.
/// </summary>
public static class RootOwnedPaths
{
    /// <summary><c>0o022</c>: the group's and the others' write bits.</summary>
    public const int GroupOrOtherWrite = 0x12;

    /// <summary><c>/</c>, then each folder down to (not including) <paramref name="path"/>'s last component.</summary>
    public static IReadOnlyList<string> Above(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return [.. Enumerable.Range(0, parts.Length).Select(n => "/" + string.Join('/', parts[..n]))];
    }

    /// <summary>The first folder of <paramref name="folders"/> somebody but root could change, with why; empty when none.</summary>
    /// <param name="where">Where those folders are, for the sentence: <c>above the drive's mount point</c>.</param>
    public static string ChainProblem(IEnumerable<string> folders, string where, Func<string, Reading<FileStatus>> stat) =>
        folders.Select(folder => FolderProblem(folder, where, stat(folder))).FirstOrDefault(p => p.Length > 0, string.Empty);

    /// <summary>Why the folder <paramref name="path"/> is not root's alone; empty when it is.</summary>
    public static string FolderProblem(string path, string where, Reading<FileStatus> status) => status switch
    {
        Reading<FileStatus>.Available { Value.IsDirectory: false } => $"{path}, {where}, is not a directory (a link or a file)",
        Reading<FileStatus>.Available { Value.OwnerUid: not 0 and var uid } => string.Create(CultureInfo.InvariantCulture, $"{path}, {where}, is owned by uid {uid}, not root"),
        Reading<FileStatus>.Available { Value.Permissions: var mode } when (mode & GroupOrOtherWrite) != 0 => $"{path}, {where}, is writable by its group or by others (mode {Convert.ToString(mode, 8)})",
        var reading => reading.ReasonOrEmpty,
    };
}
