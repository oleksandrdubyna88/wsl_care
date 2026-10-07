namespace WslCare.Core.Files;

/// <summary>
/// Plan §15r E9.S2a — what makes an opened session file copyable, judged from what the system said about the open file and
/// nothing else: a regular file (no folder, no reparse point, no FIFO or device), of ONE link (a hard link shares its bytes with a
/// name the archive never judged), owned by THIS account. Pure, so each rule is tested without a disk (gate round findings 0 and 5).
/// </summary>
internal static class ArchiveSourceRules
{
    /// <summary>What <c>GetFileInformationByHandle</c> and the file's security descriptor said about an open Windows file.</summary>
    internal readonly record struct WindowsSource(bool Ok, uint Attributes, uint Links, string Owner);

    /// <summary>Empty when the Linux file is copyable; why not otherwise.</summary>
    public static string LinuxProblem(string name, BeneathWrites.LinuxStatus status, uint me) =>
        status.Known ? LinuxShapeProblem(name, status, me) : $"{name}: its status could not be read";

    /// <summary>The Linux verified removal's checks after its hash (own review round m3: pure, complexity ≤ 4): the bytes equal the
    /// archived copy's, the write lease is still whole, and the name still names the file that was hashed — empty when it may go.</summary>
    public static string LinuxRemovalProblem(string name, bool hashEqual, bool leaseHeld, bool sameFile) =>
        !hashEqual ? $"{name}'s bytes differ from its archived copy; it stays"
        : !leaseHeld ? $"{name} was opened while it was hashed; it stays"
        : !sameFile ? $"{name} is no longer the file that was hashed; it stays"
        : string.Empty;

    /// <summary>Empty when the Windows file is copyable; why not otherwise. An owner that could not be read is never this account's.</summary>
    public static string WindowsProblem(string name, WindowsSource source, string me) =>
        !source.Ok ? $"{name}: its information could not be read"
        : WindowsShapeProblem(name, source) is { Length: > 0 } shape ? shape
        : OwnerProblem(name, source.Owner, me);

    private static string LinuxShapeProblem(string name, BeneathWrites.LinuxStatus status, uint me) =>
        !status.IsRegular ? $"{name} is not a regular file"
        : status.Links != 1 ? LinksProblem(name, status.Links)
        : status.Owner != me ? $"{name} is owned by uid {status.Owner}, not this account"
        : string.Empty;

    private static string WindowsShapeProblem(string name, WindowsSource source) =>
        (source.Attributes & (BeneathWrites.WindowsReparsePoint | BeneathWrites.WindowsDirectory)) != 0 ? $"{name} is not a regular file"
        : source.Links != 1 ? LinksProblem(name, source.Links)
        : string.Empty;

    private static string OwnerProblem(string name, string owner, string me) =>
        owner.Length > 0 && string.Equals(owner, me, StringComparison.Ordinal) ? string.Empty
        : $"{name} is not owned by this account (its owner: {(owner.Length > 0 ? owner : "unreadable")})";

    private static string LinksProblem(string name, uint links) => $"{name} has {links} links (a hard link shares its bytes with another name)";
}
