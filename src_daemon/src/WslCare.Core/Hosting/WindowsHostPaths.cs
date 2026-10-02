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

    public string TempDirectory => environment.Temp;

    public string MachineConfigFile => _rules.Join(environment.ProgramData, Product, "config.json");

    public string UserConfigFile => _rules.Join(environment.AppData, Product, "config.json");

    /// <summary>The volume the host's <c>C:</c> figure measures (plan §4.4).</summary>
    public string SystemDrive => environment.SystemDrive;

    /// <summary>Plan §4.6, the Windows column: Claude Code's three folders, Codex, Gemini CLI,
    /// Antigravity (roaming and the <c>agy</c> CLI), GitHub Copilot CLI, Rovo Dev, Ollama.</summary>
    public IReadOnlyList<string> AgentRoots { get; } =
    [
        PathRules.Windows.Join(environment.UserProfile, ".claude"),
        PathRules.Windows.Join(environment.LocalAppData, "AnthropicClaude"),
        PathRules.Windows.Join(environment.AppData, "Claude"),
        PathRules.Windows.Join(environment.UserProfile, ".codex"),
        PathRules.Windows.Join(environment.UserProfile, ".gemini"),
        PathRules.Windows.Join(environment.AppData, "Antigravity"),
        PathRules.Windows.Join(environment.LocalAppData, "agy"),
        PathRules.Windows.Join(environment.UserProfile, ".copilot"),
        PathRules.Windows.Join(environment.UserProfile, ".rovodev"),
        PathRules.Windows.Join(environment.UserProfile, ".ollama"),
    ];

    public IReadOnlyList<string> GitRoots { get; } = [PathRules.Windows.Join(environment.UserProfile, "git")];

    /// <summary>Plan §5 and the Windows plan: <c>%TEMP%\claude\</c> is never cleaned.</summary>
    public IReadOnlyList<string> ClaudeTempRoots { get; } = [PathRules.Windows.Join(environment.Temp, "claude")];
}
