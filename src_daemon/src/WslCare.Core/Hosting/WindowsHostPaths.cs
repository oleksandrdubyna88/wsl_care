namespace WslCare.Core.Hosting;

/// <summary>The folders a Windows layout is built from — the shell folders, not literals.</summary>
/// <param name="UserProfile"><c>%USERPROFILE%</c>.</param>
/// <param name="AppData"><c>%APPDATA%</c> (roaming).</param>
/// <param name="LocalAppData"><c>%LOCALAPPDATA%</c>.</param>
/// <param name="ProgramData"><c>%ProgramData%</c>.</param>
/// <param name="Temp"><c>%TEMP%</c>.</param>
/// <param name="SystemDrive">The root of the drive Windows runs from (<c>%SystemDrive%</c>), whose free
/// space the status reports as host <c>C:</c> (plan §4.4); the sandbox root under <c>WSL_CARE_ROOT</c>.</param>
public sealed record WindowsEnvironment(string UserProfile, string AppData, string LocalAppData, string ProgramData, string Temp, string SystemDrive = "C:\\")
{
    /// <summary>The Windows data folders of the manual AI agents (<c>aiAgents.extra</c>, plan §15q R2.2), protected besides the
    /// catalogue's. Empty by default.</summary>
    public IReadOnlyList<string> ExtraAgentRoots { get; init; } = [];

    /// <summary>The real machine, through the shell-folder API.</summary>
    public static WindowsEnvironment FromThisMachine() =>
        new(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            PathRules.Windows.Normalize(Path.GetTempPath()),
            Path.GetPathRoot(Environment.SystemDirectory) is { Length: > 0 } drive ? drive : "C:\\");

    /// <summary>The same layout relative to one directory — what a test or a scenario run uses so
    /// nothing real is ever read or written.</summary>
    public static WindowsEnvironment Sandboxed(string root)
    {
        var rules = PathRules.Windows;
        var profile = rules.Join(root, "Users", "me");
        var local = rules.Join(profile, "AppData", "Local");
        return new(profile, rules.Join(profile, "AppData", "Roaming"), local, rules.Join(root, "ProgramData"), rules.Join(local, "Temp"), root);
    }
}

/// <summary>Where everything lives on the Windows host (plan §6, §4.6; the Windows plan).</summary>
public sealed class WindowsHostPaths(WindowsEnvironment environment) : IHostPaths
{
    private const string Product = "wsl-care";

    private readonly PathRules _rules = PathRules.Windows;

    public HostSide Side => HostSide.Windows;

    public PathRules Rules => _rules;

    public string Home => environment.UserProfile;

    public string StateDirectory => _rules.Join(environment.LocalAppData, Product);

    public string LogDirectory => _rules.Join(environment.LocalAppData, Product, "logs");

    /// <summary>The same folder: on Windows the log directory is already the user's own.</summary>
    public string UserLogDirectory => LogDirectory;

    /// <summary><c>%USERPROFILE%\.wslconfig</c>: the VM's ceiling, read for the audit of plan §4.5 — never written
    /// (the owner's decision: the recommendation is SHOWN, never applied).</summary>
    public string WslConfigFile => _rules.Join(environment.UserProfile, ".wslconfig");

    public string TempDirectory => environment.Temp;

    /// <summary>Under the state directory: Windows has no <c>/run</c>.</summary>
    public string RunLockFile => _rules.Join(environment.LocalAppData, Product, Product + ".lock");

    public string MachineConfigFile => _rules.Join(environment.ProgramData, Product, "config.json");

    public string UserConfigFile => _rules.Join(environment.AppData, Product, "config.json");

    public string DockerDesktopConfigFile => _rules.Join(environment.UserProfile, ".docker", "daemon.json");

    /// <summary>The Windows binary does not read the distro's filesystem (plan §2: no walk through 9p).</summary>
    public string DistroPath(string absoluteLinuxPath) => string.Empty;

    /// <summary>The volume the host's <c>C:</c> figure measures (plan §4.4).</summary>
    public string SystemDrive => environment.SystemDrive;

    /// <summary>Plan §4.6, the Windows column: every Windows data folder of the agent catalogue
    /// (<see cref="Agents.AgentCatalogue"/>, E7.S1 — before it, a hand-typed list of ten).</summary>
    public IReadOnlyList<string> AgentRoots { get; } = [.. Agents.AgentCatalogue.WindowsFolders(environment), .. environment.ExtraAgentRoots];

    /// <summary>The same layout with the manual agents' Windows folders protected as well (plan §15q R2.2, M1).</summary>
    public WindowsHostPaths WithExtraAgentRoots(IReadOnlyList<string> folders) => new(environment with { ExtraAgentRoots = folders });

    /// <summary>The Windows folders the catalogue's spellings start from.</summary>
    public WindowsEnvironment Folders => environment;

    public IReadOnlyList<string> GitRoots { get; } = [PathRules.Windows.Join(environment.UserProfile, "git")];

    /// <summary>Plan §5 and the Windows plan: <c>%TEMP%\claude\</c> is never cleaned.</summary>
    public IReadOnlyList<string> ClaudeTempRoots { get; } = [PathRules.Windows.Join(environment.Temp, "claude")];
}
