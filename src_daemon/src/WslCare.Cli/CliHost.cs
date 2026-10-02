using WslCare.Core.Collectors;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;

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

    /// <summary>The real machine, or the sandbox <see cref="HostPaths.SandboxRootVariable"/> names.</summary>
    public static CliHost ForThisMachine()
    {
        var paths = HostPaths.ForThisMachine();
        return new CliHost(paths, new PhysicalFileSystem(paths), TimeProvider.System, new ProcessCommandRunner(new AllowAllCommandPolicy()));
    }

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
