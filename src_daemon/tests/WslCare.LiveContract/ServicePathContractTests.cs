using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Health;
using WslCare.Core.Processes;

namespace WslCare.LiveContract;

/// <summary>The one live check that changes this process's <c>PATH</c>, so it runs alone (every other live test resolves
/// its tool on <c>PATH</c> and would meet the narrowed one).</summary>
[CollectionDefinition(nameof(ServicePathCollection), DisableParallelization = true)]
public sealed class ServicePathCollection;

/// <summary>
/// The clock probe as the TIMER's run starts it: under the <c>PATH</c> systemd gives <c>wsl-care.service</c>, over this
/// machine's real mountinfo, binfmt_misc and files. First through the resolver with PATH as an ARGUMENT; then through the
/// product's own launcher with this process's PATH narrowed — the only way to reach the production wiring of
/// <see cref="ExecutableResolver.Resolve(string, CancellationToken)"/>. Live 2026-10-04, before the fix, this run answered
/// "powershell.exe was not found on PATH (5 directories searched, executable files only)".
/// </summary>
[Collection(nameof(ServicePathCollection))]
public sealed class ServicePathContractTests
{
    /// <summary>The PATH systemd gives a service whose unit sets none — no Windows folder on it.</summary>
    private const string ServicePath = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/snap/bin";

    [Fact]
    public async Task Under_a_services_minimal_path_the_windows_clock_probe_is_found_on_the_system_drive_and_answers()
    {
        if (!IsWsl(out var why))
        {
            Live.Unavailable($"the system drive fallback is WSL's, and this is not a WSL distro: {why}");
        }

        // Decided independently of the code under test (binfmt_misc) — so here the drive MUST be found.
        WindowsSystemDrive.MountHere().Should().BeOfType<Reading<SystemDriveMount>.Available>("this is a WSL distro, so its system drive is mounted");
        var resolved = ExecutableResolver.Resolve(HealthCommands.PowerShell, ServicePath, windows: false, SystemDriveLookup.ThisMachine, TestContext.Current.CancellationToken);
        resolved.Should().BeOfType<ResolvedExecutable.Found>("the resolver answered {0}", resolved).Which.OnTheSystemDrive.Should().BeTrue();

        var previous = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", ServicePath);
        try
        {
            var outcome = await Live.RunAsync(HealthCommands.WindowsClock);
            var answer = ToolAnswers.Read(HealthCommands.WindowsClock, outcome).Bind(HealthParsers.WindowsClock);

            answer.Should().BeOfType<Reading<WindowsClockAnswer>.Available>("the probe answered {0}", answer)
                .Which.Value.Profile.Should().MatchRegex(@"^[A-Za-z]:\\");
            outcome.StartedFrom.Should().Be(((ResolvedExecutable.Found)resolved).Path, "the launcher started the file the resolver found, and says so");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previous);
        }
    }

    /// <summary>WSL by its own mark: its interop handler registered in binfmt_misc. "microsoft" in <c>/proc/version</c> is NOT
    /// used: a Docker Desktop container shares WSL's kernel and reads the same string with no drive mounted.</summary>
    private static bool IsWsl(out string why)
    {
        if (OperatingSystem.IsWindows())
        {
            why = "this binary runs on Windows";
            return false;
        }

        why = $"neither {string.Join(" nor ", WindowsSystemDrive.InteropEntries)} exists";
        return WindowsSystemDrive.InteropEntries.Any(File.Exists);
    }
}
