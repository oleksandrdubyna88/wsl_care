using System.Reflection;

using FluentAssertions;

using WslCare.Cli;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// The whole program in-process, with its streams captured: what goes to stdout, what goes to
/// stderr, and the exit code.
/// </summary>
public sealed class ProgramTests
{
    [Fact]
    public void Version_prints_the_assembly_informational_version_and_nothing_else()
    {
        // Read from the attribute itself, not through the code under test.
        var expected = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        expected.Should().NotBeNullOrWhiteSpace("the SDK stamps every assembly it builds");
        using var sandbox = new SandboxHost("program-version");

        var (exit, stdout, stderr) = CliRun.Over(sandbox, "--version");

        exit.Should().Be(0);
        stdout.Should().Be(expected + Environment.NewLine);
        stderr.Should().BeEmpty();
    }

    [Fact]
    public void Help_exits_zero_and_writes_the_help_text_to_stdout_only()
    {
        using var sandbox = new SandboxHost("program-help");

        var (exit, stdout, stderr) = CliRun.Over(sandbox, "--help");

        exit.Should().Be(0);
        stdout.Should().Be(CommandLine.HelpText + Environment.NewLine);
        stdout.Should().Contain("wsl-care config get").And.Contain("wsl-care config set").And.Contain("wsl-care config reset");
        stderr.Should().BeEmpty();
    }

    [Fact]
    public void An_unknown_verb_exits_non_zero_with_exactly_one_line_on_stderr_and_nothing_on_stdout()
    {
        using var sandbox = new SandboxHost("program-unknown");

        var (exit, stdout, stderr) = CliRun.Over(sandbox, "frobnicate");

        exit.Should().NotBe(0).And.Be((int)ExitCode.Usage);
        stdout.Should().BeEmpty();
        CliRun.Lines(stderr).Should().ContainSingle()
            .Which.Should().StartWith("wsl-care: ").And.Contain("frobnicate");
    }

    [Fact]
    public void An_unknown_verb_carrying_a_newline_is_still_reported_on_one_line()
    {
        using var sandbox = new SandboxHost("program-newline");

        var (exit, _, stderr) = CliRun.Over(sandbox, "two\nlines");

        exit.Should().Be((int)ExitCode.Usage);
        CliRun.Lines(stderr).Should().ContainSingle();
    }

    [Fact]
    public void A_cancelled_token_stops_the_run_before_the_verb_as_a_cancellation()
    {
        using var sandbox = new SandboxHost("program-cancel");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var act = () => CliRun.Over(sandbox, Serilog.Core.Logger.None, cancelled.Token, "config", "get");

        act.Should().Throw<OperationCanceledException>();
    }
}
