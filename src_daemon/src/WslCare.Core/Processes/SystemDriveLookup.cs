using WslCare.Core.Collectors;

namespace WslCare.Core.Processes;

/// <summary>
/// Everything <see cref="ExecutableResolver"/> asks when it looks for a Windows program on the mounted system drive, as
/// four questions and a ceiling — so a test can answer each from a fixture and the product answers each from this machine
/// (<see cref="ThisMachine"/>).
/// </summary>
/// <param name="Mount">Where the system drive is mounted (<see cref="WindowsSystemDrive.MountHere"/>).</param>
/// <param name="InteropRefusal">Empty when WSL interop is registered and enabled; otherwise why not.</param>
/// <param name="MountPointRefusal">Empty when nobody but root can change what is under the mount point; otherwise why.</param>
/// <param name="Inspect">Empty when the program's file may be started; otherwise why not (mount, folder, name).</param>
/// <param name="Ceiling">How long all four may take together before the lookup is refused.</param>
public sealed record SystemDriveLookup(
    Func<Reading<SystemDriveMount>> Mount,
    Func<string> InteropRefusal,
    Func<string, string> MountPointRefusal,
    Func<SystemDriveMount, string, string, string> Inspect,
    TimeSpan Ceiling)
{
    /// <summary>This machine's mountinfo, binfmt_misc and files — what the product's <see cref="ExecutableResolver.Resolve(string, CancellationToken)"/> asks.
    /// Built per use, so its ceiling is the one in force (<c>commands.systemDriveLookupSeconds</c>), never one frozen at first use.</summary>
    public static SystemDriveLookup ThisMachine => new(
        WindowsSystemDrive.MountHere, WindowsSystemDrive.InteropRefusalHere, SystemDriveFiles.MountPointRefusal, SystemDriveFiles.Problem, WindowsSystemDrive.Ceiling);

    /// <summary>No system drive at all: the lookup is PATH alone (built per use, like <see cref="ThisMachine"/>).</summary>
    public static SystemDriveLookup NotConsulted => new(
        () => Reading.Missing<SystemDriveMount>("this lookup consults PATH alone"), () => string.Empty, _ => string.Empty, (_, _, _) => string.Empty, WindowsSystemDrive.Ceiling);
}
