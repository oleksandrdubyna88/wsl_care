using System.Diagnostics;
using System.Text;

using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Health;

namespace WslCare.Core.Tests.Health;

/// <summary>
/// E15.S1b (the plan round): the ACTUAL Windows disk script, exactly as the daemon's pwsh member of the closed set carries it
/// (<see cref="WindowsDiskCommands.WindowsDiskUnderPwsh"/>), started under the REAL MSI PowerShell 7 at
/// <c>%ProgramFiles%\PowerShell\7\pwsh.exe</c> and read by the product's parser. This is the evidence that choosing pwsh is
/// safe to ship: the answer is complete (<c>end=1</c>), no line is malformed, and the Storage sections answer — which they do
/// only because the script imports Storage by its absolute System32 path (with the module path pinned, pwsh 7 has no
/// <c>Get-Volume</c> of its own; measured 2026-10-12).
/// </summary>
/// <remarks>Windows only, and only where the MSI build is installed: it SKIPS on the owner's machine (only the Store pwsh there)
/// and RUNS on CI's <c>windows-latest</c>, which ships PowerShell 7 at that path. Read-only: the script reads, prints and ends.
/// The program is started by its absolute path here because the test is about THAT file; the daemon starts the bare
/// <c>pwsh.exe</c>, which the resolver finds in the same folder on the system drive.</remarks>
public sealed class RealPwshDiskScriptTests
{
    /// <summary>A cold pwsh start on a CI runner plus the script's own reads — longer than the product's 30 s ceiling, which a
    /// warm machine meets; the test is about the answer, not the time.</summary>
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(120);

    private static string MsiPwsh => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", HealthCommands.Pwsh);

    [Fact]
    public async Task The_actual_disk_script_under_the_msi_pwsh_answers_every_section_the_parser_reads()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the MSI PowerShell 7 is a Windows program; the Linux legs run the script through the fake");
        Assert.SkipUnless(File.Exists(MsiPwsh), $"{MsiPwsh} is absent: no MSI PowerShell 7 on this machine (the owner's has only the Store build); CI's windows-latest has it");

        var stdout = await RunAsync(MsiPwsh, WindowsDiskCommands.WindowsDiskUnderPwsh.Arguments);

        stdout.Should().EndWith(WindowsDiskCommands.EndLine + "\n", "a complete answer ends with its end line");
        var facts = Available(WindowsDiskParsers.Parse(stdout, WindowsDiskCommands.EventsMaxRead, WindowsDiskCommands.EventsSinceDays));
        facts.MalformedLines.Should().Be(0, "every line the script prints under pwsh 7 is one the parser reads");
        Available(facts.Volumes).Should().Contain(v => v.Letter == "C", "Get-Volume answers: the Storage import worked");
        Available(facts.PhysicalDisks).Should().NotBeEmpty("Get-PhysicalDisk answers: the Storage import worked");
        (facts.Reliability.IsAvailable || facts.Reliability.ReasonOrEmpty.Contains("elevation", StringComparison.Ordinal)).Should().BeTrue(facts.Reliability.ReasonOrEmpty);
        Available(facts.Events).Read.Should().BeLessThanOrEqualTo(WindowsDiskCommands.EventsMaxRead);
        Available(facts.CommitLimitBytes).Should().BePositive();
        Available(facts.Identity.MachineGuid).Should().NotBeEmpty();
        Available(facts.Identity.Account).Should().StartWith("S-1-5-");
        // The Lxss map is about WSL: a machine without it may say why, but the section is still read or explained.
        (facts.Distros.IsAvailable || facts.Distros.ReasonOrEmpty.Length > 0).Should().BeTrue();
    }

    private static T Available<T>(Reading<T> reading)
    {
        reading.Should().BeOfType<Reading<T>.Available>(reading.ReasonOrEmpty);
        return ((Reading<T>.Available)reading).Value;
    }

    /// <summary>Starts <paramref name="program"/> with <paramref name="arguments"/> as an argument LIST (no shell, no quoting),
    /// reads stdout as UTF-8 (the script writes UTF-8 bytes on the raw stream), and kills the tree past <see cref="Ceiling"/>.</summary>
    private static async Task<string> RunAsync(string program, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{program} did not start");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Ceiling);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        process.ExitCode.Should().Be(0, await stderr);
        return await stdout;
    }
}
