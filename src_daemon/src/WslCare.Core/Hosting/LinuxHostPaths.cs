namespace WslCare.Core.Hosting;

/// <summary>The folders a Linux (WSL distro) layout is built from.</summary>
/// <param name="Home">The user's home.</param>
/// <param name="Etc">Machine configuration (<c>/etc</c>).</param>
/// <param name="Var">Variable state (<c>/var</c>).</param>
/// <param name="Tmp">The temporary directory (<c>/tmp</c>).</param>
/// <param name="ConfigHome">Where user configuration goes (<c>$XDG_CONFIG_HOME</c>, else <c>~/.config</c>).</param>
/// <param name="Root">The filesystem root the collectors read under: <c>/proc</c>, <c>/sys/fs/cgroup</c>,
/// and the volume <c>df /</c> measures (plan §4.1, §4.2, §4.4). <c>/</c> on the real machine; the sandbox
/// root under <c>WSL_CARE_ROOT</c>, so a test or a scenario hands the collectors a captured fixture tree.</param>
/// <param name="StateHome">Where a user's own state goes (<c>$XDG_STATE_HOME</c>, else <c>~/.local/state</c>) — the
/// log root of a run that may not write <c>/var/log/wsl-care</c> (plan §15b #3); empty means the default under
/// <paramref name="Home"/>.</param>
public sealed record LinuxEnvironment(string Home, string Etc, string Var, string Tmp, string ConfigHome, string Root = "/", string StateHome = "")
{
    /// <summary>The real machine: <c>$HOME</c>, <c>/etc</c>, <c>/var</c>, <c>/tmp</c>.</summary>
    public static LinuxEnvironment FromThisMachine()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var configHome = string.IsNullOrWhiteSpace(xdg) ? PathRules.Linux.Join(home, ".config") : xdg;
        var xdgState = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        return new(home, "/etc", "/var", "/tmp", configHome, "/", string.IsNullOrWhiteSpace(xdgState) ? string.Empty : xdgState);
    }

    /// <summary>The same layout relative to one directory — what a test or a scenario run uses so
    /// nothing real is ever read or written.</summary>
    public static LinuxEnvironment Sandboxed(string root)
    {
        var rules = PathRules.Linux;
        var home = rules.Join(root, "home", "me");
        return new(home, rules.Join(root, "etc"), rules.Join(root, "var"), rules.Join(root, "tmp"), rules.Join(home, ".config"), root);
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

    /// <summary><c>$XDG_STATE_HOME/wsl-care/logs</c>, by default <c>~/.local/state/wsl-care/logs</c> (plan §15b #3).</summary>
    public string UserLogDirectory =>
        _rules.Join(environment.StateHome.Length > 0 ? environment.StateHome : _rules.Join(environment.Home, ".local", "state"), Product, "logs");

    public string TempDirectory => environment.Tmp;

    public string MachineConfigFile => _rules.Join(environment.Etc, Product, "config.json");

    public string UserConfigFile => _rules.Join(environment.ConfigHome, Product, "config.json");

    /// <summary>Not readable from inside the distro; see <see cref="IHostPaths.DockerDesktopConfigFile"/>.</summary>
    public string DockerDesktopConfigFile => string.Empty;

    public string DistroPath(string absoluteLinuxPath) => _rules.Join(environment.Root, absoluteLinuxPath.TrimStart('/'));

    /// <summary>The procfs mount the memory and process collectors read (<c>/proc</c>).</summary>
    public string ProcRoot => _rules.Join(environment.Root, "proc");

    /// <summary>The cgroup v2 mount the container collector reads (<c>/sys/fs/cgroup</c>).</summary>
    public string CgroupRoot => _rules.Join(environment.Root, "sys", "fs", "cgroup");

    /// <summary>What <c>df /</c> measures: the distro's root filesystem (plan §4.4).</summary>
    public string FilesystemRoot => environment.Root;

    /// <summary>The user database the process collector names owners from (<c>/etc/passwd</c>).</summary>
    public string PasswdFile => _rules.Join(environment.Etc, "passwd");

    /// <summary>WSL's per-distro settings (<c>/etc/wsl.conf</c>): its <c>[automount] root</c> says where Windows drives
    /// appear (<c>/mnt/</c> by default) — how a Windows path the clock probe printed becomes a path here.</summary>
    public string WslConfFile => _rules.Join(environment.Etc, "wsl.conf");

    /// <summary>The apt package cache A9 measures (<c>/var/cache/apt</c>).</summary>
    public string AptCacheDirectory => _rules.Join(environment.Var, "cache", "apt");

    /// <summary>Where snapd keeps each revision's squashfs file (<c>/var/lib/snapd/snaps</c>): A9's disabled revisions.</summary>
    public string SnapFilesDirectory => _rules.Join(environment.Var, "lib", "snapd", "snaps");

    /// <summary>sysstat's daily data files (<c>/var/log/sysstat</c>); the newest one's last write says whether it still collects (plan §4.5).</summary>
    public string SysstatDirectory => _rules.Join(environment.Var, "log", "sysstat");

    /// <summary>atop's daily raw files (<c>/var/log/atop</c>), read the same way.</summary>
    public string AtopDirectory => _rules.Join(environment.Var, "log", "atop");

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
