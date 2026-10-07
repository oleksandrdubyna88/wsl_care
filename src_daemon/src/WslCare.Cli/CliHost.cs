using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Agents;
using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Mcp;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;

namespace WslCare.Cli;

/// <summary>
/// Everything a verb reaches the machine through: where things are, the disk, the clock, the
/// process launcher, and the probe of this binary's side. One record, built once in <c>Main</c> — and
/// built over a temporary root by the tests, so a verb never knows which it got.
/// </summary>
/// <remarks>The probe is derived from the layout unless a test sets it (<c>with { Probe = … }</c>): a
/// Linux layout gets the <see cref="LinuxProbe"/> reading that layout's <c>/proc</c> and cgroup roots, a
/// Windows layout the <see cref="WindowsProbe"/>. Neither holds the <see cref="Commands"/> runner, which
/// is how <c>status</c> is kept from starting a process (plan §15b #5) by construction.</remarks>
internal sealed record CliHost(IHostPaths Paths, IFileSystem Files, TimeProvider Clock, ICommandRunner Commands)
{
    public IHostProbe Probe { get; init; } = ProbeFor(Paths, Files, Clock);

    /// <summary>Whether this process is root — what <c>act</c> refuses without (plan §15c #0). A test sets it.</summary>
    public ProcessPrivilege Privilege { get; init; } = ProcessPrivilege.OfThisProcess();

    /// <summary>The operating system's process table, for the <c>running.json</c> liveness check. A test scripts it.</summary>
    public IProcessTable Processes { get; init; } = new SystemProcessTable();

    /// <summary>The Windows process table the Windows binary's <c>status</c> counts its MCP servers in (E14 S7a, read-only). Reads nothing
    /// unless <see cref="ForThisMachine"/> wires the real one on Windows, so a test's answer never depends on this machine's processes.</summary>
    public IWindowsProcessTable WindowsProcesses { get; init; } = new UnreadWindowsProcessTable("a host built by a test reads no Windows process table");

    /// <summary>How a verb waits a measuring window (the MCP servers' CPU window, plan §15q E7.S2d). A test hands one that returns
    /// at once and changes what the second read sees.</summary>
    public Func<TimeSpan, CancellationToken, Task> Wait { get; init; } = static (delay, token) => Task.Delay(delay, token);

    /// <summary>The archive's seam (plan §15r E9.S2a): the same file system, seen through its archive verbs.</summary>
    public IArchiveFiles ArchiveFiles() => Files as IArchiveFiles ?? throw new InvalidOperationException($"the file system {Files.GetType().Name} has no archive seam");

    /// <summary>The archive protocol's fault seam (<see cref="Core.Archive.MoveSteps"/>): nothing on a machine; under a sandbox
    /// <see cref="ArchiveKillVariable"/> names the step at which the process KILLS itself — the scenario suite's 14 kill points.</summary>
    public Action<string> ArchiveFault { get; init; } = static _ => { };

    /// <summary>Honoured only together with <see cref="HostPaths.SandboxRootVariable"/>: <c>&lt;step&gt;</c> or <c>&lt;step&gt;#&lt;n&gt;</c> (its n-th time).</summary>
    public const string ArchiveKillVariable = "WSL_CARE_TEST_ARCHIVE_KILL";

    /// <summary>The actions this build holds.</summary>
    public ActionRegistry Actions { get; init; } = ActionRegistry.Product;

    /// <summary>How A11 signals a process. Refuses unless <see cref="ForThisMachine"/> wires the real sender — which it does
    /// only inside the distro and never under a sandbox, whose process table is a fixture (E3.S2).</summary>
    public IProcessSignals Signals { get; init; } = RefusingProcessSignals.NotWired;

    /// <summary>Whose home the per-user paths follow (plan §15c #2): the target user's when root.</summary>
    public HomeOwner HomeOwner { get; init; } = new HomeOwner.ThisProcess("a host built by a test");

    /// <summary>Why WSL interop is unavailable here — empty when it is (plan §15q R1.2: without it another account's layer may
    /// only tighten a root run). Asked only for a root run; <see cref="ForThisMachine"/> wires the distro's binfmt_misc, a host
    /// built by a test says "available".</summary>
    public Func<string> InteropRefusal { get; init; } = static () => string.Empty;

    /// <summary>The configuration this host runs under: the three layers, trusted as <see cref="UserLayerTrusts"/> decides — or,
    /// root with an ambiguous target user, the defaults and the machine layer only (gate finding #2: user-scoped actions refuse,
    /// machine-scoped ones still run).</summary>
    public ConfigLoadResult LoadConfig() =>
        ConfigLoader.Load(Paths, Files, UserLayerTrusts.For(HomeOwner, InteropRefusal, rootTimerReadsThisLayer: Paths is LinuxHostPaths && !Privilege.IsRoot));

    /// <summary>What cancelled this process, in words — asked only once it was cancelled; <c>Main</c> wires
    /// <see cref="ShutdownSignals.Cause"/>, so an interrupted run's record names the signal (plan §15j B2).</summary>
    public Func<string> InterruptCause { get; init; } = static () => "a signal";

    /// <summary>This process's stdin — what <c>act … --only -</c> reads A4's shown list from (E6.S1). A test hands its own stream.</summary>
    public Func<Stream> StandardInput { get; init; } = Console.OpenStandardInput;

    /// <summary>How long <c>--only -</c> waits for the end of stdin (<c>act.stdinTimeoutSeconds</c>, read when asked — the host is
    /// built before the configuration is loaded); a test shortens it.</summary>
    public TimeSpan StdinCeiling { get => field == TimeSpan.Zero ? StdinList.Ceiling : field; init; }

    /// <summary>The file system and the signal sender for another layout of the same machine — what the second phase of the
    /// host (<see cref="WithAgentExtras"/>) rebuilds once the configuration names the manual agents' folders (plan §15q R2.2,
    /// review M1). A host built by a test keeps what it was given.</summary>
    public Func<IHostPaths, CliHost, (IFileSystem Files, IProcessSignals Signals)> Rewire { get; init; } = static (_, host) => (host.Files, host.Signals);

    /// <summary>Phase two of the host (plan §15q R2.2, review M1): the deletion policy is built with the file system, which phase
    /// one built BEFORE the configuration was read — so once <paramref name="config"/> names manual AI agents, the layout gains
    /// their data folders as protected roots (every shape-valid entry of this side, whether or not it passes the walk's rules —
    /// review B2) and the file system and what holds it are rebuilt over it. The same host when there are none.</summary>
    public (CliHost Host, ConfigLoadResult Loaded) WithAgentExtras(ConfigLoadResult loaded)
    {
        var (host, dropped) = Protecting(loaded.Config);
        // coai E7 code round #5: a folder phase two did not protect is a structured notice of THIS load, never host state.
        return dropped.Count == 0
            ? (host, loaded)
            : (host, loaded with { Notices = [.. loaded.Notices, .. dropped.Select(m => new ConfigNotice(new ConfigLayerFile(ConfigLayer.User, Paths.UserConfigFile), 0, ConfigKeys.AiAgents.Extra.Name, m))] });
    }

    private (CliHost Host, IReadOnlyList<string> Dropped) Protecting(EffectiveConfig config)
    {
        var extras = config.Agents(ConfigKeys.AiAgents.Extra);
        var chosen = Paths switch
        {
            LinuxHostPaths linux => ExtraRoots.ForDistro(linux, Files, Folders(extras, ExtraAgentShape.Wsl)),
            WindowsHostPaths windows => ExtraRoots.ForWindows(windows, Files, Folders(extras, ExtraAgentShape.Windows)),
            _ => ExtraRoots.None,
        };
        if (chosen.Kept.Count == 0)
        {
            return (this, chosen.Dropped);
        }

        var paths = Paths switch
        {
            LinuxHostPaths linux => (IHostPaths)linux.WithExtraAgentRoots(chosen.Kept),
            WindowsHostPaths windows => windows.WithExtraAgentRoots(chosen.Kept),
            _ => Paths,
        };
        var (files, signals) = Rewire(paths, this);
        return (this with { Paths = paths, Files = files, Signals = signals }, chosen.Dropped);
    }

    private static IReadOnlyList<string> Folders(IReadOnlyList<ExtraAgent> extras, string side) =>
        [.. extras.Where(e => e.Side == side).SelectMany(e => e.DataFolders).Distinct(StringComparer.Ordinal)];

    /// <summary>The real machine, or the sandbox <see cref="HostPaths.SandboxRootVariable"/> names. The runner is the
    /// product's ONE policy (<see cref="CommandPolicy.Product"/>: the never-list over the declared templates); inside the
    /// distro, as root, the per-user paths are the TARGET user's (E3.S2), and every login account's home is protected besides
    /// it (plan §15c #2).</summary>
    public static CliHost ForThisMachine()
    {
        var privilege = ProcessPrivilege.OfThisProcess();
        var (owned, owner) = HostPaths.ForThisMachine() switch
        {
            LinuxHostPaths linux => TargetHome.Resolve(linux, new PhysicalFileSystem(linux), privilege.IsRoot),
            var other => (other, (HomeOwner)new HomeOwner.ThisProcess("the Windows binary")),
        };
        var paths = WithLoginHomesProtected(owned);
        // State files another process trusts are root's on a machine; under a sandbox (WSL_CARE_ROOT: the scenarios) the
        // state there is this process's own (E6.S0 review S1).
        var files = FilesFor(paths);
        return new CliHost(paths, files, TimeProvider.System, new ProcessCommandRunner(CommandPolicy.Product))
        {
            ArchiveFault = KillFault(),
            Rewire = static (layout, _) => FilesAndSignalsFor(layout),
            Privilege = privilege,
            HomeOwner = owner,
            Signals = SignalsFor(paths, files),
            WindowsProcesses = WindowsProcessesFor(paths),
            InteropRefusal = () => paths is LinuxHostPaths linux ? UserLayerTrusts.InteropRefusal(linux, files) : string.Empty,
        };
    }

    /// <summary>The real signal sender inside the distro; a refusing one under a sandbox (a fixture's pids are not this
    /// machine's) and on Windows.</summary>
    private static bool Sandboxed() => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(HostPaths.SandboxRootVariable));

    private static IProcessSignals SignalsFor(IHostPaths paths, IFileSystem files) =>
        Sandboxed() ? RefusingProcessSignals.Sandboxed
        : paths is LinuxHostPaths linux && OperatingSystem.IsLinux() ? new PidfdProcessSignals(files, linux.ProcRoot)
        : new RefusingProcessSignals("the Windows binary signals no process (A11 is the distro's)");

    private static (IFileSystem, IProcessSignals) FilesAndSignalsFor(IHostPaths paths)
    {
        var files = FilesFor(paths);
        return (files, SignalsFor(paths, files));
    }

    private static PhysicalFileSystem FilesFor(IHostPaths paths)
    {
        var fault = KillFault();
        return new(paths, PhysicalFileSystem.ReadLinkTarget, static (_, _) => { }, (step, _) => fault(step.ToString()))
        {
            TrustedStateOwner = Sandboxed() ? RegularFiles.EffectiveUid() : 0,
            OwnersAreThisProcess = Sandboxed(),
        };
    }

    /// <summary>The archive kill point a scenario names (<see cref="ArchiveKillVariable"/>, sandbox only): at that step — its n-th
    /// time — this process kills ITSELF (by its own handle, never by a name), as a crash would. Nothing on a machine.</summary>
    internal static Action<string> KillFault()
    {
        var spec = Sandboxed() ? Environment.GetEnvironmentVariable(ArchiveKillVariable) ?? string.Empty : string.Empty;
        var parts = spec.Split('#');
        var nth = parts.Length > 1 && int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 1;
        var seen = 0;
        return spec.Length == 0 ? static _ => { }
        : name =>
        {
            if (name == parts[0] && Interlocked.Increment(ref seen) == nth)
            {
                using var self = System.Diagnostics.Process.GetCurrentProcess();
                self.Kill();
            }
        };
    }

    private static IHostPaths WithLoginHomesProtected(IHostPaths paths) =>
        paths is LinuxHostPaths linux ? linux.WithProtectedHomes(TargetUserDiscovery.ProtectedHomes(new PhysicalFileSystem(linux), linux)) : paths;

    /// <summary>The real Windows process table for the Windows binary; none in the distro (its MCP servers come from <c>/proc</c>).</summary>
    private static IWindowsProcessTable WindowsProcessesFor(IHostPaths paths) =>
        paths is WindowsHostPaths && OperatingSystem.IsWindows()
            ? new Win32ProcessTable()
            : new UnreadWindowsProcessTable("the distro's binary reads the distro's MCP servers (mcpServers)");

    private static IHostProbe ProbeFor(IHostPaths paths, IFileSystem files, TimeProvider clock)
    {
        if (paths is LinuxHostPaths linux)
        {
            return new LinuxProbe(files, linux, clock);
        }

        if (paths is WindowsHostPaths windows && OperatingSystem.IsWindows())
        {
            return new WindowsProbe(files, windows, new Win32Counters(), clock);
        }

        throw new PlatformNotSupportedException($"no probe for a {paths.GetType().Name} layout on this operating system");
    }
}
