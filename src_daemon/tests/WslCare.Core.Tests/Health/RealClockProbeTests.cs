using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Health;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;

namespace WslCare.Core.Tests.Health;

/// <summary>
/// E15.S1b, the final code round (finding 8): the ACTUAL clock probe (<see cref="HealthCommands.WindowsClock"/>) — its control
/// characters cleaned, its clock lines flushed before the pwsh look, the look itself — started under the REAL Windows
/// PowerShell 5.1 through the product's own runner and policy, and read by the product's parsers. Read-only: the script reads
/// two clocks, a service's state and one file's signature, prints, and ends.
/// </summary>
/// <remarks>Windows only: in the distro the probe goes through WSL interop, which the live contract covers. It runs on the owner's
/// machine and on CI's <c>windows-latest</c>. Only the ceiling is the test's (a cold start on a loaded machine).</remarks>
public sealed class RealClockProbeTests
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task The_actual_clock_probe_under_windows_powershell_exits_0_is_measured_and_prints_each_pwsh_tag_once()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the probe reaches powershell.exe through WSL interop in the distro; the live contract runs that");
        var runner = new ProcessCommandRunner(CommandPolicy.Product);
        var command = HealthCommands.WindowsClock with { Ceiling = Ceiling };

        var outcome = await runner.RunAsync(command.ToRequest(), TestContext.Current.CancellationToken);

        var exited = outcome.Should().BeOfType<CommandOutcome.Exited>("{0}", outcome).Which;
        exited.ExitCode.Should().Be(0, exited.Stderr.Text);
        var lines = exited.Stdout.Text.Split('\n').Select(l => l.Trim()).ToList();
        foreach (var tag in new[] { HealthCommands.PwshKnownFolderTag, HealthCommands.PwshEnvProgramFilesTag, HealthCommands.PwshMsiTag, HealthCommands.PwshSignatureTag })
        {
            lines.Count(l => l.StartsWith(tag, StringComparison.Ordinal)).Should().Be(1, "{0} is printed exactly once", tag);
        }

        var answer = HealthParsers.WindowsClock(exited.Stdout.Text).Should().BeOfType<Reading<WindowsClockAnswer>.Available>().Which.Value;
        answer.Pwsh.IsAvailable.Should().BeTrue(answer.Pwsh.ReasonOrEmpty);
        answer.Profile.Should().NotBeEmpty();
        var sample = await HealthCollector.MeasureWindowsClockAsync(runner, TimeProvider.System, TestContext.Current.CancellationToken);
        sample.Measured.Should().BeTrue(sample.Unavailable);
    }
}
