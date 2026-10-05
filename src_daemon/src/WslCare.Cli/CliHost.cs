using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
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

    /// <summary>How long <c>--only -</c> waits for the end of stdin (plan §15j M2: 10 s); a test shortens it.</summary>
    public TimeSpan StdinCeiling { get; init; } = StdinList.Ceiling;

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
        var files = new PhysicalFileSystem(paths) { TrustedStateOwner = Sandboxed() ? RegularFiles.EffectiveUid() : 0, OwnersAreThisProcess = Sandboxed() };
        return new CliHost(paths, files, TimeProvider.System, new ProcessCommandRunner(CommandPolicy.Product))
        {
            Privilege = privilege,
            HomeOwner = owner,
            Signals = SignalsFor(paths, files),
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

    private static IHostPaths WithLoginHomesProtected(IHostPaths paths) =>
        paths is LinuxHostPaths linux ? linux.WithProtectedHomes(TargetUserDiscovery.ProtectedHomes(new PhysicalFileSystem(linux), linux)) : paths;

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
