namespace WslCare.Core.Hosting;

/// <summary>Which side of the machine a binary runs on (plan §2): the WSL distro, or the Windows host.</summary>
public enum HostSide
{
    Wsl,
    Windows,
}

/// <summary>
/// Every path the daemon reads or writes, per operating system — the one place a location is
/// decided (plan §15a C3: never a literal in shared code).
/// </summary>
/// <remarks>
/// <para>Two implementations, <see cref="LinuxHostPaths"/> and <see cref="WindowsHostPaths"/>, and
/// one factory, <see cref="HostPaths.ForThisMachine()"/>. A test never touches the real folders: it
/// builds either implementation over a temporary root, and the built binary does the same when
/// <see cref="HostPaths.SandboxRootVariable"/> is set.</para>
/// <para>The protected roots — where an AI agent keeps its sessions, where the repositories are, the
/// folder Claude Code keeps under the temp directory — are here too, because the deletion policy
/// reads them from the same source the collectors do. One list, two readers.</para>
/// </remarks>
public interface IHostPaths
{
    HostSide Side { get; }

    /// <summary>The path conventions of this host's operating system.</summary>
    PathRules Rules { get; }

    /// <summary>The user's home (<c>$HOME</c> / <c>%USERPROFILE%</c>).</summary>
    string Home { get; }

    /// <summary>Where run history and durable state live (plan §6).</summary>
    string StateDirectory { get; }

    /// <summary>The root of the per-run log files (plan §6; family logging rule).</summary>
    string LogDirectory { get; }

    /// <summary>The system temporary directory.</summary>
    string TempDirectory { get; }

    /// <summary>The machine-wide configuration layer (plan §6).</summary>
    string MachineConfigFile { get; }

    /// <summary>The user's configuration layer, the one <c>config set</c> writes (plan §6).</summary>
    string UserConfigFile { get; }

    /// <summary>The data folders of the AI agents of plan §4.6 — never deleted under, by anything.</summary>
    IReadOnlyList<string> AgentRoots { get; }

    /// <summary>Where the repositories live — never deleted under (plan §5, the never list).</summary>
    IReadOnlyList<string> GitRoots { get; }

    /// <summary>The folder Claude Code keeps under the temp directory — never cleaned.</summary>
    IReadOnlyList<string> ClaudeTempRoots { get; }
}
