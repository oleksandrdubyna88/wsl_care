namespace WslCare.Core.Hosting;

/// <summary>The folders a Linux (WSL distro) layout is built from.</summary>
/// <param name="Home">The user's home.</param>
/// <param name="Etc">Machine configuration (<c>/etc</c>).</param>
/// <param name="Var">Variable state (<c>/var</c>).</param>
/// <param name="Tmp">The temporary directory (<c>/tmp</c>).</param>
/// <param name="ConfigHome">Where user configuration goes (<c>$XDG_CONFIG_HOME</c>, else <c>~/.config</c>).</param>
public sealed record LinuxEnvironment(string Home, string Etc, string Var, string Tmp, string ConfigHome)
{
    /// <summary>The real machine: <c>$HOME</c>, <c>/etc</c>, <c>/var</c>, <c>/tmp</c>.</summary>
    public static LinuxEnvironment FromThisMachine()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var configHome = string.IsNullOrWhiteSpace(xdg) ? PathRules.Linux.Join(home, ".config") : xdg;
        return new(home, "/etc", "/var", "/tmp", configHome);
    }

    /// <summary>The same layout relative to one directory — what a test or a scenario run uses so
    /// nothing real is ever read or written.</summary>
    public static LinuxEnvironment Sandboxed(string root)
    {
        var rules = PathRules.Linux;
        var home = rules.Join(root, "home", "me");
        return new(home, rules.Join(root, "etc"), rules.Join(root, "var"), rules.Join(root, "tmp"), rules.Join(home, ".config"));
    }
}

/// <summary>Where everything lives inside the WSL distro (plan §6, §4.6).</summary>
public sealed class LinuxHostPaths(LinuxEnvironment environment) : IHostPaths
{
    private const string Product = "wsl-care";

    private readonly PathRules _rules = PathRules.Linux;

    public HostSide Side => HostSide.Wsl;

    public PathRules Rules => _rules;

    public string Home => environment.Home;

    public string StateDirectory => _rules.Join(environment.Var, "lib", Product);

    public string LogDirectory => _rules.Join(environment.Var, "log", Product);

    public string TempDirectory => environment.Tmp;

    public string MachineConfigFile => _rules.Join(environment.Etc, Product, "config.json");

    public string UserConfigFile => _rules.Join(environment.ConfigHome, Product, "config.json");

    /// <summary>Plan §4.6, the Linux column: Claude Code, Codex, Gemini CLI, Antigravity's cache,
    /// GitHub Copilot CLI, Rovo Dev, Ollama's models. The agent catalogue (E7) extends this list.</summary>
    public IReadOnlyList<string> AgentRoots { get; } =
    [
        PathRules.Linux.Join(environment.Home, ".claude"),
        PathRules.Linux.Join(environment.Home, ".codex"),
        PathRules.Linux.Join(environment.Home, ".gemini"),
        PathRules.Linux.Join(environment.Home, ".cache", "antigravity"),
        PathRules.Linux.Join(environment.Home, ".copilot"),
        PathRules.Linux.Join(environment.Home, ".rovodev"),
        PathRules.Linux.Join(environment.Home, ".ollama"),
    ];

    public IReadOnlyList<string> GitRoots { get; } = [PathRules.Linux.Join(environment.Home, "git")];

    /// <summary>Claude Code keeps its shell snapshots and task output under <c>$TMPDIR/claude</c>.</summary>
    public IReadOnlyList<string> ClaudeTempRoots { get; } = [PathRules.Linux.Join(environment.Tmp, "claude")];
}
