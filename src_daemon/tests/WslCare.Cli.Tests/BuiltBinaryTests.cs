using System.Diagnostics;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Hosting;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// The BUILT <c>wsl-care</c> executable, run as a separate process — the real exit code through
/// the operating system, the real streams, the real apphost, the real signal and logging wiring in
/// <c>Main</c> — rather than <see cref="Program.Run"/> called in-process.
/// </summary>
/// <remarks>
/// The SDK copies a referenced executable project's apphost, <c>.deps.json</c> and
/// <c>.runtimeconfig.json</c> beside the test assembly, so the binary under test is the one the
/// <c>ProjectReference</c> built. This is the JIT build; the Native AOT binary is smoked by CI after
/// <c>dotnet publish</c>. Every run gets <c>WSL_CARE_ROOT</c>, so the real configuration and log
/// folders of this machine are never read or written.
/// </remarks>
public sealed class BuiltBinaryTests
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task The_built_binary_answers_help_on_stdout_with_exit_code_zero()
    {
        using var sandbox = new TempRoot("bin-help");

        var result = await RunBuiltBinaryAsync(sandbox, "--help");

        result.Exit.Should().Be(0);
        result.Stdout.Should().Contain("wsl-care --help").And.Contain("wsl-care --version").And.Contain("wsl-care config get");
        result.Stderr.Should().BeEmpty();
    }

    [Fact]
    public async Task The_built_binary_refuses_an_unknown_verb_with_a_non_zero_exit_code_and_one_stderr_line()
    {
        using var sandbox = new TempRoot("bin-unknown");

        var result = await RunBuiltBinaryAsync(sandbox, "frobnicate");

        result.Exit.Should().Be((int)ExitCode.Usage);
        result.Stdout.Should().BeEmpty();
        result.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().ContainSingle();
    }

    [Fact]
    public async Task The_built_binary_reads_and_writes_its_configuration_under_the_sandbox_root_and_writes_a_run_log_there()
    {
        using var sandbox = new TempRoot("bin-config");
        var paths = HostPaths.ForThisMachine(sandbox.Path);

        var set = await RunBuiltBinaryAsync(sandbox, "config", "set", "dryRun", "false");
        var get = await RunBuiltBinaryAsync(sandbox, "config", "get", "dryRun", "--json");
        var refused = await RunBuiltBinaryAsync(sandbox, "config", "set", "dryRun", "sometimes");

        set.Exit.Should().Be(0);
        File.Exists(paths.UserConfigFile).Should().BeTrue("the user layer must land where the sandboxed host says, never in the real profile");
        get.Exit.Should().Be(0);
        get.Stdout.Should().Contain("\"schemaVersion\": 1").And.Contain("\"layer\": \"user\"").And.Contain("\"value\": false");
        refused.Exit.Should().Be((int)ExitCode.Usage);
        refused.Stderr.Should().Contain("dryRun must be true or false");
        var logs = Directory.GetFiles(paths.LogDirectory, "wsl-care-*.log", SearchOption.AllDirectories);
        logs.Should().HaveCount(3, "every run of the built binary writes its own log file");
        logs.Should().OnlyContain(log => File.ReadAllText(log).Contains("ConfigSet") || File.ReadAllText(log).Contains("ConfigGet"), "a run log says what the run did");
    }

    [Fact]
    public async Task Help_and_version_touch_nothing_under_the_sandbox_root()
    {
        using var sandbox = new TempRoot("bin-quick");

        await RunBuiltBinaryAsync(sandbox, "--help");
        await RunBuiltBinaryAsync(sandbox, "--version");
        await RunBuiltBinaryAsync(sandbox, "frobnicate");

        Directory.EnumerateFileSystemEntries(sandbox.Path).Should().BeEmpty("a question about the binary is not a run and opens no log file");
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunBuiltBinaryAsync(TempRoot sandbox, params string[] args)
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
        start.Environment[HostPaths.SandboxRootVariable] = sandbox.Path;
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
