using System.Reflection;

using FluentAssertions;

using WslCare.Cli;

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

        var (exit, stdout, stderr) = Run("--version");

        exit.Should().Be(0);
        stdout.Should().Be(expected + Environment.NewLine);
        stderr.Should().BeEmpty();
    }

    [Fact]
    public void Help_exits_zero_and_writes_the_help_text_to_stdout_only()
    {
        var (exit, stdout, stderr) = Run("--help");

        exit.Should().Be(0);
        stdout.Should().Be(CommandLine.HelpText + Environment.NewLine);
        stderr.Should().BeEmpty();
    }

    [Fact]
    public void An_unknown_verb_exits_non_zero_with_exactly_one_line_on_stderr_and_nothing_on_stdout()
    {
        var (exit, stdout, stderr) = Run("frobnicate");

        exit.Should().NotBe(0).And.Be((int)ExitCode.Usage);
        stdout.Should().BeEmpty();
        Lines(stderr).Should().ContainSingle()
            .Which.Should().StartWith("wsl-care: ").And.Contain("frobnicate");
    }

    [Fact]
    public void An_unknown_verb_carrying_a_newline_is_still_reported_on_one_line()
    {
        var (exit, _, stderr) = Run("two\nlines");

        exit.Should().Be((int)ExitCode.Usage);
        Lines(stderr).Should().ContainSingle();
    }

    private static (int Exit, string Stdout, string Stderr) Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = Program.Run(args, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Lines as a terminal shows them: ANY line break counts, not only this platform's
    /// <see cref="Environment.NewLine"/> — splitting on "\r\n" alone let a bare "\n" pass as one line.</summary>
    private static string[] Lines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
}
