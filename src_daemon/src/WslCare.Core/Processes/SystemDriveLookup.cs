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

    /// <summary>The lookup the product's runner asks for THIS process: <see cref="ThisMachine"/>, except for an unprivileged process
    /// inside a sandbox (<c>WSL_CARE_ROOT</c>, a test or a scenario), which gets the sandbox's own drive folder.</summary>
    public static SystemDriveLookup ForThisProcess =>
        Choose(Environment.GetEnvironmentVariable(Hosting.HostPaths.SandboxRootVariable), Environment.IsPrivilegedProcess);

    /// <summary>
    /// The seam of E15.S1b, as a pure choice: <see cref="Sandboxed"/> only when <paramref name="sandboxRoot"/> is set AND the
    /// process is NOT really privileged (root by the operating system, never by the sandbox's claim). A real root process —
    /// the timer's, or a root collect through <c>wsl.exe</c>, whose environment the Windows user can reach through
    /// <c>WSLENV</c> — always asks this machine, so no environment variable can point it at a user-writable folder.
    /// </summary>
    public static SystemDriveLookup Choose(string? sandboxRoot, bool privileged) =>
        string.IsNullOrWhiteSpace(sandboxRoot) || privileged ? ThisMachine : Sandboxed(sandboxRoot);

    /// <summary>
    /// The sandbox's "system drive": <c>&lt;sandbox root&gt;/mnt/c</c>, interop taken as enabled, and a program there startable when
    /// it is a regular file (no link) this account may start. What it stands for: the scenario harness reaches a program that
    /// is NEVER looked up on PATH (<see cref="WindowsSystemDrive.FixedFolderOnly"/>) in its fixed folder under the sandbox — the
    /// fake is not a Windows program, so the drive's <c>MZ</c> and device checks would refuse it. Only an unprivileged process
    /// gets this (<see cref="Choose"/>): starting a file the same account could have started anyway raises nothing.
    /// </summary>
    public static SystemDriveLookup Sandboxed(string sandboxRoot) => new(
        () => Reading.Of(new SystemDriveMount(Path.Combine(sandboxRoot, "mnt", "c"), 0, 0)),
        () => string.Empty,
        _ => string.Empty,
        SandboxedFile,
        WindowsSystemDrive.Ceiling);

    /// <summary>Empty when the sandbox's file is a regular file, not a link, that this account may start; otherwise why not.</summary>
    private static string SandboxedFile(SystemDriveMount mount, string folder, string name)
    {
        var path = Path.Combine(mount.MountPoint, folder, name);
        return ExecutableResolver.IsStartable(path, windows: false) && new FileInfo(path).LinkTarget is null
            ? string.Empty
            : $"{path} is not a startable regular file in the sandbox";
    }

    /// <summary>No system drive at all: the lookup is PATH alone (built per use, like <see cref="ThisMachine"/>).</summary>
    public static SystemDriveLookup NotConsulted => new(
        () => Reading.Missing<SystemDriveMount>("this lookup consults PATH alone"), () => string.Empty, _ => string.Empty, (_, _, _) => string.Empty, WindowsSystemDrive.Ceiling);
}
