namespace WslCare.Core.Hosting;

/// <summary>Chooses the <see cref="IHostPaths"/> for the machine this process runs on.</summary>
public static class HostPaths
{
    /// <summary>
    /// When set, every path — configuration layers, state, logs, temp, the protected roots — is
    /// laid out under this directory instead of the real machine. The scenario harness (E1.S3) and
    /// the process-level tests run the built binary with it; nothing real is touched.
    /// </summary>
    public const string SandboxRootVariable = "WSL_CARE_ROOT";

    /// <summary>The real layout of this operating system, or the sandbox the environment names.</summary>
    public static IHostPaths ForThisMachine() =>
        ForThisMachine(Environment.GetEnvironmentVariable(SandboxRootVariable));

    /// <summary>The same decision with the variable's value passed in, so it is a unit test.</summary>
    public static IHostPaths ForThisMachine(string? sandboxRoot)
    {
        var sandboxed = !string.IsNullOrWhiteSpace(sandboxRoot);
        if (OperatingSystem.IsWindows())
        {
            return new WindowsHostPaths(sandboxed ? WindowsEnvironment.Sandboxed(sandboxRoot!) : WindowsEnvironment.FromThisMachine());
        }

        return new LinuxHostPaths(sandboxed ? LinuxEnvironment.Sandboxed(sandboxRoot!) : LinuxEnvironment.FromThisMachine());
    }
}
