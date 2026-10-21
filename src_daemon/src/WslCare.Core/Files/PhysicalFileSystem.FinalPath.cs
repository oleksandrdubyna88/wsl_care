using WslCare.Core.Collectors;

namespace WslCare.Core.Files;

public sealed partial class PhysicalFileSystem
{
    /// <summary>Windows: the folder HELD (<see cref="BeneathWrites.HoldFolderWindows"/> — the folder itself, a link opened as itself, never followed, and
    /// pinned while the handle lives) and its final path asked of the handle, which the system spells with every 8.3 short name, <c>subst</c> drive and
    /// trailing dot expanded. Elsewhere the path as given: no other platform spells one folder two ways through this seam.</summary>
    public Reading<string> FolderFinalPath(string path) => OperatingSystem.IsWindows() ? FinalPathOfHeldFolder(path) : Reading.Of(path);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Reading<string> FinalPathOfHeldFolder(string path)
    {
        using var held = BeneathWrites.HoldFolderWindows(path);
        return held switch
        {
            NativeOpen.Opened opened when BeneathWrites.FinalPath(opened.Handle) is { Length: > 0 } final => Reading.Of(final),
            NativeOpen.Opened => Reading.Missing<string>($"{path} was opened, but the system could not say what it is called"),
            NativeOpen.Failed failed => Reading.Missing<string>($"{path} could not be opened to read its real name (error {failed.Error})"),
            _ => throw new System.Diagnostics.UnreachableException("NativeOpen is a closed set"),
        };
    }
}
