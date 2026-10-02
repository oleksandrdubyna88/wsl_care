using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Collectors;
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

    /// <summary>The real machine, or the sandbox <see cref="HostPaths.SandboxRootVariable"/> names. The runner is the
    /// product's ONE policy (<see cref="CommandPolicy.Product"/>: the never-list over the declared templates); inside the
    /// distro every login account's home is protected besides <c>$HOME</c> (plan §15c #2).</summary>
    public static CliHost ForThisMachine()
    {
        var paths = WithLoginHomesProtected(HostPaths.ForThisMachine());
        return new CliHost(paths, new PhysicalFileSystem(paths), TimeProvider.System, new ProcessCommandRunner(CommandPolicy.Product));
    }

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
