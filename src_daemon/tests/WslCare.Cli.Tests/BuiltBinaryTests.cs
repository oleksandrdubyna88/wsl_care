using System.Diagnostics;

using FluentAssertions;

using WslCare.Cli;

namespace WslCare.Cli.Tests;

/// <summary>
/// The BUILT <c>wsl-care</c> executable, run as a separate process — the real exit code through
/// the operating system, the real streams, the real apphost — rather than <see cref="Program.Run"/>
/// called in-process.
/// </summary>
/// <remarks>
/// The SDK copies a referenced executable project's apphost, <c>.deps.json</c> and
/// <c>.runtimeconfig.json</c> beside the test assembly, so the binary under test is the one the
/// <c>ProjectReference</c> built. This is the JIT build; the Native AOT binary is smoked by CI after
/// <c>dotnet publish</c>.
/// </remarks>
public sealed class BuiltBinaryTests
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task The_built_binary_answers_help_on_stdout_with_exit_code_zero()
    {
        var result = await RunBuiltBinaryAsync("--help");

        result.Exit.Should().Be(0);
        result.Stdout.Should().Contain("wsl-care --help").And.Contain("wsl-care --version");
        result.Stderr.Should().BeEmpty();
    }

    [Fact]
    public async Task The_built_binary_refuses_an_unknown_verb_with_a_non_zero_exit_code_and_one_stderr_line()
    {
        var result = await RunBuiltBinaryAsync("frobnicate");

        result.Exit.Should().Be((int)ExitCode.Usage);
        result.Stdout.Should().BeEmpty();
        result.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().ContainSingle();
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunBuiltBinaryAsync(params string[] args)
    {
        var binary = Path.Combine(
            AppContext.BaseDirectory,
            CommandLine.BinaryName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        File.Exists(binary).Should().BeTrue($"the referenced CLI's apphost should have been copied to {binary}");

        var start = new ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"could not start {binary}");
        using var deadline = new CancellationTokenSource(Ceiling);
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // A timeout that only stops WAITING leaves the child running; kill the whole tree.
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{binary} did not exit within {Ceiling.TotalSeconds:0} s");
        }

        return (process.ExitCode, await stdout, await stderr);
    }
}
