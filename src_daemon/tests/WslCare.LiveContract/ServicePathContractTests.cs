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
/// The clock probe as the TIMER's run starts it: under the <c>PATH</c> systemd gives <c>wsl-care.service</c>, through the
/// product's own launcher — so the lookup is the product's <see cref="ExecutableResolver.Resolve(string)"/> over this
/// machine's real mount table and files, not a test's copy of either. Live 2026-10-04, before the fix, this run answered
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
        if (OperatingSystem.IsWindows())
        {
            Live.Unavailable("the system drive fallback is the distro's; this binary runs on Windows");
        }

        if (WindowsSystemDrive.MountPointHere() is Reading<string>.Unavailable { Reason: var notWsl })
        {
            Live.Unavailable($"no Windows system drive is mounted here, so this is not a WSL distro: {notWsl}");
        }

        var previous = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", ServicePath);
        try
        {
            var answer = ToolAnswers.Read(HealthCommands.WindowsClock, await Live.RunAsync(HealthCommands.WindowsClock)).Bind(HealthParsers.WindowsClock);

            answer.Should().BeOfType<Reading<WindowsClockAnswer>.Available>("the probe answered {0}", answer)
                .Which.Value.Profile.Should().MatchRegex(@"^[A-Za-z]:\\");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previous);
        }
    }
}
