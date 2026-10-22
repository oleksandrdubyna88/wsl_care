using WslCare.Core.Health;

namespace WslCare.Core.Collectors;

/// <summary>One drive as <c>DriveInfo</c> names it — the seam under <see cref="WindowsVolumes"/>, so a test scripts a network drive, a drive
/// with no disk in it and a drive that throws without touching this machine's.</summary>
public interface IDrive
{
    /// <summary>The root as Windows spells it (<c>C:\</c>).</summary>
    string Name { get; }

    DriveType DriveType { get; }

    bool IsReady { get; }

    long TotalSize { get; }

    long AvailableFreeSpace { get; }

    string DriveFormat { get; }

    string VolumeLabel { get; }
}

/// <summary>A real <see cref="DriveInfo"/> as an <see cref="IDrive"/>.</summary>
public sealed class SystemDrive(DriveInfo drive) : IDrive
{
    public string Name => drive.Name;

    public DriveType DriveType => drive.DriveType;

    public bool IsReady => drive.IsReady;

    public long TotalSize => drive.TotalSize;

    public long AvailableFreeSpace => drive.AvailableFreeSpace;

    public string DriveFormat => drive.DriveFormat;

    public string VolumeLabel => drive.VolumeLabel;
}

/// <summary>
/// The real <see cref="IWindowsVolumes"/> (E19.S2): <c>DriveInfo.GetDrives()</c>, no process. Only <c>Fixed</c> and <c>Removable</c> drives are
/// volumes — a <c>Network</c> drive is dropped by its type ALONE, never asked anything (a mapped drive that is down can block a read for the
/// whole network timeout, and status must stay fast), and <c>CDRom</c>, <c>Ram</c>, <c>NoRootDirectory</c> and <c>Unknown</c> hold no space
/// worth a warning. A drive that is not ready (a card reader with no card) or whose figures throw stays in the table, its size and free space
/// unread WITH the reason: "not read" is never 0, and the rule above then judges it <c>unknown</c>, never <c>ok</c>.
/// </summary>
public sealed class WindowsVolumes(Func<IEnumerable<IDrive>> drives) : IWindowsVolumes
{
    /// <summary>Why a volume has no disk number: it is <c>Get-Partition</c>'s, which the native reading does not run.</summary>
    public const string NotReadNatively = "not read natively";

    public WindowsVolumes()
        : this(static () => DriveInfo.GetDrives().Select(static d => new SystemDrive(d)))
    {
    }

    public Reading<IReadOnlyList<WindowsVolume>> Read()
    {
        try
        {
            return Reading.Of<IReadOnlyList<WindowsVolume>>([.. drives().SelectMany(VolumeOf).OrderBy(v => v.Letter, StringComparer.Ordinal)]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Reading.Missing<IReadOnlyList<WindowsVolume>>($"the drives could not be listed: {e.Message}");
        }
    }

    /// <summary>The drive as a volume — or nothing when it is not a lettered fixed or removable drive (an empty answer is "skip", never null).</summary>
    private static IEnumerable<WindowsVolume> VolumeOf(IDrive drive) =>
        drive.DriveType is DriveType.Fixed or DriveType.Removable && LetterOf(drive.Name) is { Length: 1 } letter ? [Measure(drive, letter)] : [];

    /// <summary><c>C:\</c> (or <c>C:</c>) is drive C; anything else — a mount path, a UNC root — is no lettered volume.</summary>
    private static string LetterOf(string name) =>
        name.Length is 2 or 3 && char.IsAsciiLetter(name[0]) && name[1] == ':' && (name.Length == 2 || name[2] == '\\') ? char.ToUpperInvariant(name[0]).ToString() : string.Empty;

    private static WindowsVolume Measure(IDrive drive, string letter)
    {
        try
        {
            return drive.IsReady
                ? new(letter, NoDiskNumber, Text(() => drive.DriveFormat), Reading.Of(drive.TotalSize), Reading.Of(drive.AvailableFreeSpace), Text(() => drive.VolumeLabel))
                : Unread(letter, $"{letter}: is not ready (no disk in the drive, or the volume is not mounted)");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Unread(letter, $"{letter}: could not be read: {e.Message}");
        }
    }

    private static Reading<int> NoDiskNumber => Reading.Missing<int>(NotReadNatively);

    private static WindowsVolume Unread(string letter, string why) =>
        new(letter, NoDiskNumber, string.Empty, Reading.Missing<long>(why), Reading.Missing<long>(why), string.Empty);

    /// <summary>A text (the file system, the label) that cannot be read is empty: it only describes the volume, it decides nothing.</summary>
    private static string Text(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
