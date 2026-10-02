using FluentAssertions;

using WslCare.Cli;

namespace WslCare.Scenarios;

/// <summary>The two questions about the binary itself, asked of the BUILT CLI.</summary>
public sealed class HelpAndVersionFlows
{
    [Fact]
    public async Task Help_exits_zero_and_lists_every_registered_command_on_stdout()
    {
        using var home = new ScenarioHome("help");

        var result = await home.RunAsync("--help");

        result.Exit.Should().Be((int)ExitCode.Ok);
        result.Stderr.Should().BeEmpty();
        foreach (var command in CommandLine.Commands)
        {
            result.Stdout.Should().Contain($"wsl-care {command.Usage}", "the help text is derived from the register");
        }

        home.Calls.Should().BeEmpty("a question about the binary runs no tool");
        Directory.EnumerateFileSystemEntries(home.SandboxRoot).Should().BeEmpty("--help is not a run and opens no log file");
    }

    [Fact]
    public async Task Version_prints_the_version_in_src_daemon_version_txt()
    {
        using var home = new ScenarioHome("version");
        var expected = File.ReadAllText(ScenarioHome.Stamped("WslCare.VersionFile")).Trim();

        var result = await home.RunAsync("--version");

        result.Exit.Should().Be((int)ExitCode.Ok);
        result.Stderr.Should().BeEmpty();
        var printed = result.StdoutLines.Should().ContainSingle().Subject;
        // The SDK may append "+<commit>" (source link); the release number is what must match.
        printed.Split('+')[0].Should().Be(expected);
        home.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_verb_is_refused_with_the_usage_code_and_one_stderr_line()
    {
        using var home = new ScenarioHome("unknown");

        var result = await home.RunAsync("frobnicate");

        result.Exit.Should().Be((int)ExitCode.Usage);
        result.Stdout.Should().BeEmpty();
        result.StderrLines.Should().ContainSingle().Which.Should().Contain("\"frobnicate\"");
        home.Calls.Should().BeEmpty();
    }
}
