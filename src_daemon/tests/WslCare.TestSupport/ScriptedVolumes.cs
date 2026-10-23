using WslCare.Core.Collectors;
using WslCare.Core.Health;

namespace WslCare.TestSupport;

/// <summary>
/// The lettered Windows volumes as a test scripts them (E19.S2): the table it is given, and how many times it was asked — so a test can
/// say a binary read the drives, or never did. Nothing of the machine the test runs on is read.
/// </summary>
public sealed class ScriptedVolumes(Reading<IReadOnlyList<WindowsVolume>> table) : IWindowsVolumes
{
    private int _calls;

    public const long Gib = 1L << 30;

    /// <summary>How many times <see cref="Read"/> was called.</summary>
    public int Calls => Volatile.Read(ref _calls);

    public static ScriptedVolumes Of(params WindowsVolume[] volumes) => new(Reading.Of<IReadOnlyList<WindowsVolume>>(volumes));

    public static ScriptedVolumes Unread(string why) => new(Reading.Missing<IReadOnlyList<WindowsVolume>>(why));

    /// <summary>The system drive at 953 GiB with 114 GiB free, and a 30 MB keyboard stick: the recorded machine's shape.</summary>
    public static ScriptedVolumes Machine() => Of(Volume("C", 953 * Gib, 114 * Gib, "NTFS", "System"), Volume("E", 30_000_000, 12_000_000, "FAT", "KEYBOARD"));

    /// <summary>One volume with both figures read and no disk number (the native reading never has one).</summary>
    public static WindowsVolume Volume(string letter, long size, long free, string fileSystem = "NTFS", string label = "") =>
        new(letter, Reading.Missing<int>(WindowsVolumes.NotReadNatively), fileSystem, Reading.Of(size), Reading.Of(free), label);

    public Reading<IReadOnlyList<WindowsVolume>> Read()
    {
        Interlocked.Increment(ref _calls);
        return table;
    }
}
