using FluentAssertions;

using WslCare.Cli;

namespace WslCare.Cli.Tests;

/// <summary>
/// Argument parsing: what each spelling asks for, and what is refused rather than guessed at.
/// </summary>
public sealed class CommandLineTests
{
    [Fact]
    public void No_arguments_prints_help_rather_than_an_error()
    {
        CommandLine.Parse([]).Should().BeOfType<Request.Help>();
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    public void Every_documented_spelling_of_help_asks_for_help(string spelling)
    {
        CommandLine.Parse([spelling]).Should().BeOfType<Request.Help>();
    }

    [Fact]
    public void The_version_flag_asks_for_the_version()
    {
        CommandLine.Parse(["--version"]).Should().BeOfType<Request.Version>();
    }

    [Fact]
    public void An_unknown_verb_names_itself_and_points_at_help()
    {
        var failed = CommandLine.Parse(["frobnicate"]).Should().BeOfType<Request.Failed>().Subject;

        failed.Message.Should().Contain("\"frobnicate\"").And.Contain("wsl-care --help");
    }

    [Fact]
    public void Matching_is_exact_so_a_near_miss_is_refused_rather_than_guessed()
    {
        CommandLine.Parse(["--Version"]).Should().BeOfType<Request.Failed>();
        CommandLine.Parse(["-version"]).Should().BeOfType<Request.Failed>();
    }

    [Fact]
    public void A_flag_given_extra_words_is_refused_rather_than_ignoring_them()
    {
        // Silently dropping "status" would answer a question nobody asked.
        var failed = CommandLine.Parse(["--version", "status"]).Should().BeOfType<Request.Failed>().Subject;

        failed.Message.Should().Contain("--version");
    }

    [Fact]
    public void A_control_character_in_an_unknown_verb_never_reaches_the_message()
    {
        var failed = (Request.Failed)CommandLine.Parse(["bad\nverb\u001b[2J"]);

        failed.Message.Should().NotContainAny("\n", "\r", "\u001b");
        failed.Message.Should().Contain("bad?verb?[2J");
    }

    [Fact]
    public void Every_registered_command_is_in_the_help_text_and_is_accepted_by_its_usage_spelling()
    {
        // Derived from the register, not retyped: a command added there is checked here for free.
        CommandLine.Commands.Should().NotBeEmpty();
        foreach (var command in CommandLine.Commands)
        {
            CommandLine.HelpText.Should().Contain($"wsl-care {command.Usage}");
            command.Spellings.Should().Contain(command.Usage);
            CommandLine.Parse([command.Usage]).Should().Be(command.Answer);
        }
    }
}
