namespace WslCare.Core.Hosting;

/// <summary>
/// Whether this process is root (plan §15c #0: every <c>act</c> runs as root) — the operating system's answer
/// (<see cref="Environment.IsPrivilegedProcess"/>: effective uid 0 on Linux, an elevated token on Windows), and why.
/// </summary>
/// <remarks>
/// <para><b>The sandbox claim.</b> Under <c>WSL_CARE_ROOT</c> (a test, a scenario, a smoke), <see cref="SandboxVariable"/>=1
/// makes this process ANSWER root without being it, so the harness can drive <c>act</c> over a temporary root with fake
/// tools on <c>PATH</c>. It changes only this answer — never what the operating system lets the process do — and every
/// path the run touches is under the sandbox root. Outside a sandbox the variable is ignored.</para>
/// </remarks>
public sealed record ProcessPrivilege(bool IsRoot, string Basis)
{
    /// <summary>Honoured only together with <see cref="HostPaths.SandboxRootVariable"/>.</summary>
    public const string SandboxVariable = "WSL_CARE_SANDBOX_PRIVILEGED";

    public static ProcessPrivilege OfThisProcess() =>
        Decide(Environment.GetEnvironmentVariable(HostPaths.SandboxRootVariable), Environment.GetEnvironmentVariable(SandboxVariable), Environment.IsPrivilegedProcess, OperatingSystem.IsWindows());

    /// <summary>The same decision with its inputs passed in, so it is a unit test.</summary>
    public static ProcessPrivilege Decide(string? sandboxRoot, string? sandboxClaim, bool privileged, bool windows)
    {
        if (!string.IsNullOrWhiteSpace(sandboxRoot) && sandboxClaim == "1")
        {
            return new ProcessPrivilege(true, $"claimed by {SandboxVariable}=1 inside the sandbox {HostPaths.SandboxRootVariable}");
        }

        return privileged
            ? new ProcessPrivilege(true, windows ? "this process is elevated" : "this process runs as root (effective uid 0)")
            : new ProcessPrivilege(false, windows ? "this process is not elevated" : "this process does not run as root (its effective uid is not 0)");
    }
}
