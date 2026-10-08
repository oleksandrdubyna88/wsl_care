using WslCare.Core.Collectors;
using WslCare.Core.Files;

namespace WslCare.Core.Processes;

/// <summary>Where the product's own binary is, checked — or why it may not be started as another user.</summary>
public abstract record SelfBinaryResult
{
    private SelfBinaryResult()
    {
    }

    /// <summary>The full path, every check of <see cref="SelfBinary"/> passed.</summary>
    public sealed record Found(string Path) : SelfBinaryResult;

    public sealed record Refused(string Reason) : SelfBinaryResult;
}

/// <summary>
/// The product's OWN installed binary, the only executable a self-invocation template starts (plan §15r D1, E9.S4): its path is
/// the running process's (<see cref="Environment.ProcessPath"/>, which the kernel resolves through <c>/proc/self/exe</c> —
/// <c>/opt/wsl-care/bin/wsl-care</c> when installed), never a name looked up in the target user's bin folders, where
/// <c>~/.local/bin/wsl-care</c> would be the user's own file. It is accepted only when the file is a regular file (never a
/// link) named <see cref="Name"/>, owned by root, writable by neither group nor others and executable, and every folder from
/// <c>/</c> down to it is root's alone (<see cref="RootOwnedPaths"/>) — so nobody but root can change what root starts as the
/// user between the check and the start.
/// </summary>
/// <remarks>Checked again at every build of a request and every review of one (a check cached across them would be a window).
/// The file is started by path after the check (.NET starts no descriptor); on a tree only root can write, that window is
/// root's own. The Windows binary never starts itself as another user: there it is refused.</remarks>
public static class SelfBinary
{
    /// <summary>The binary's file name — what a self-invocation template declares as its executable.</summary>
    public const string Name = "wsl-care";

    private const int AnyExecute = 0x49; // 0o111

    private const string Where = "on the way to the product's own binary";

    /// <summary>This process's binary, checked now.</summary>
    public static SelfBinaryResult Product() =>
        OperatingSystem.IsLinux()
            ? LinuxProduct()
            : new SelfBinaryResult.Refused("the Windows binary never starts itself as another user");

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static SelfBinaryResult LinuxProduct() => Locate(Environment.ProcessPath ?? string.Empty, RegularFiles.StatNoFollow);

    /// <summary><paramref name="path"/> checked through <paramref name="stat"/> (a status that never follows a final link).</summary>
    public static SelfBinaryResult Locate(string path, Func<string, Reading<FileStatus>> stat) =>
        Problem(path, stat) is { Length: > 0 } problem ? new SelfBinaryResult.Refused(problem) : new SelfBinaryResult.Found(path);

    /// <summary>Why <paramref name="path"/> may not be started as the target user; empty when every check holds.</summary>
    public static string Problem(string path, Func<string, Reading<FileStatus>> stat) =>
        ShapeProblem(path) is { Length: > 0 } shape ? shape
        : RootOwnedPaths.ChainProblem(RootOwnedPaths.Above(path), Where, stat) is { Length: > 0 } chain ? chain
        : FileProblem(path, stat(path));

    /// <summary>A full path with no <c>.</c> or <c>..</c> component, ending in <see cref="Name"/>.</summary>
    private static string ShapeProblem(string path) =>
        !path.StartsWith('/') ? $"the product's binary has no full path (\"{path}\")"
        : path.Split('/').Any(s => s is "." or "..") ? $"the product's binary path {path} holds . or .."
        : !string.Equals(Path.GetFileName(path), Name, StringComparison.Ordinal) ? $"the product's binary {path} is not named {Name}"
        : string.Empty;

    private static string FileProblem(string path, Reading<FileStatus> status) => status switch
    {
        Reading<FileStatus>.Available { Value: var file } => KindProblem(path, file) is { Length: > 0 } kind ? kind : ModeProblem(path, file.Permissions),
        var reading => reading.ReasonOrEmpty,
    };

    /// <summary>A regular file, owned by root.</summary>
    private static string KindProblem(string path, FileStatus file) =>
        !file.IsRegular ? $"the product's binary {path} is not a regular file (a link, a folder or a device)"
        : file.OwnerUid is not 0 ? $"the product's binary {path} is not owned by root"
        : string.Empty;

    /// <summary>Writable by neither group nor others, and executable.</summary>
    private static string ModeProblem(string path, int mode) =>
        (mode & RootOwnedPaths.GroupOrOtherWrite) != 0 ? $"the product's binary {path} is writable by its group or by others (mode {Convert.ToString(mode, 8)})"
        : (mode & AnyExecute) == 0 ? $"the product's binary {path} has no execute bit"
        : string.Empty;
}
