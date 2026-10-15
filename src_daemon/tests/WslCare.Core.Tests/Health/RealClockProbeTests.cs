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

    /// <summary>
    /// Security review (Fable, S2), live: a profile holding U+25D9 — which Windows PowerShell 5.1's <c>Console.Out</c> in a fresh
    /// IBM437 console maps by best fit to a real line feed (measured 2026-10-15) — followed by <c>pwsh.msi=file</c>, and a Cyrillic
    /// name. Printed as UTF-8 bytes on the raw stream, the profile comes back as ONE line, unchanged, and no tag is forged.
    /// </summary>
    /// <remarks>This one starts the product's own probe arguments through <see cref="System.Diagnostics.Process"/>, not the shared
    /// runner: the hostile value must reach the child through ITS environment, and the runner has no environment seam — adding one
    /// for a test would widen the product's launcher. A hidden new console is what the interop launch sees: the OEM code page.</remarks>
    [Fact]
    public async Task A_hostile_profile_comes_back_as_one_line_and_forges_no_tag()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the probe reaches powershell.exe through WSL interop in the distro; the live contract runs that");
        var profile = "C:\\Users\\\u0424\u0451\u0434\u043e\u0440\u25D9pwsh.msi=file";
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", HealthCommands.PowerShell))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in HealthCommands.WindowsClock.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["USERPROFILE"] = profile;
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("powershell.exe did not start");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Ceiling);
        var stdout = await process.StandardOutput.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);

        var lines = stdout.Split('\n').Select(l => l.Trim()).ToList();
        lines[2].Should().Be(profile, "the profile is one line, its non-ASCII letters intact (stdout: {0})", stdout);
        lines.Count(l => l.StartsWith(HealthCommands.PwshMsiTag, StringComparison.Ordinal)).Should().Be(1, "no forged pwsh.msi line");
        HealthParsers.WindowsClock(stdout).Should().BeOfType<Reading<WindowsClockAnswer>.Available>().Which.Value.Pwsh.IsAvailable.Should().BeTrue();
    }
}
