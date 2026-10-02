using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;

namespace WslCare.Cli;

/// <summary>
/// Everything a verb reaches the machine through: where things are, the disk, the clock, and the
/// process launcher. One record, built once in <c>Main</c> — and built over a temporary root by the
/// tests, so a verb never knows which it got.
/// </summary>
internal sealed record CliHost(IHostPaths Paths, IFileSystem Files, TimeProvider Clock, ICommandRunner Commands)
{
    /// <summary>The real machine, or the sandbox <see cref="HostPaths.SandboxRootVariable"/> names.</summary>
    public static CliHost ForThisMachine()
    {
        var paths = HostPaths.ForThisMachine();
        return new CliHost(paths, new PhysicalFileSystem(paths), TimeProvider.System, new ProcessCommandRunner(new AllowAllCommandPolicy()));
    }
}
