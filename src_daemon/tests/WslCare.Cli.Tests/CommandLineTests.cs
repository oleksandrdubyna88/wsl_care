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
        CommandLine.Parse(["Config", "get"]).Should().BeOfType<Request.Failed>();
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
    public void Config_get_takes_an_optional_key_and_the_json_flag_in_either_order()
    {
        CommandLine.Parse(["config", "get"]).Should().Be(new Request.ConfigGet(string.Empty, Json: false));
        CommandLine.Parse(["config", "get", "dryRun"]).Should().Be(new Request.ConfigGet("dryRun", Json: false));
        CommandLine.Parse(["config", "get", "--json"]).Should().Be(new Request.ConfigGet(string.Empty, Json: true));
        CommandLine.Parse(["config", "get", "dryRun", "--json"]).Should().Be(new Request.ConfigGet("dryRun", Json: true));
        CommandLine.Parse(["config", "get", "--json", "dryRun"]).Should().Be(new Request.ConfigGet("dryRun", Json: true));
    }

    [Fact]
    public void Config_get_refuses_a_second_key_and_an_unknown_option()
    {
        CommandLine.Parse(["config", "get", "a", "b"]).Should().BeOfType<Request.Failed>().Which.Message.Should().Contain("at most one key");
        CommandLine.Parse(["config", "get", "--yaml"]).Should().BeOfType<Request.Failed>().Which.Message.Should().Contain("--yaml");
    }

    [Fact]
    public void Config_set_takes_exactly_a_key_and_a_value()
    {
        CommandLine.Parse(["config", "set", "dryRun", "false"]).Should().Be(new Request.ConfigSet("dryRun", "false"));
        CommandLine.Parse(["config", "set", "dryRun"]).Should().BeOfType<Request.Failed>().Which.Message.Should().Contain("config set <key> <value>");
        CommandLine.Parse(["config", "set", "dryRun", "false", "extra"]).Should().BeOfType<Request.Failed>();
    }

    [Fact]
    public void Config_reset_takes_exactly_one_key()
    {
        CommandLine.Parse(["config", "reset", "dryRun"]).Should().Be(new Request.ConfigReset("dryRun"));
        CommandLine.Parse(["config", "reset"]).Should().BeOfType<Request.Failed>().Which.Message.Should().Contain("config reset <key>");
    }

    [Fact]
    public void Config_alone_or_with_an_unknown_sub_verb_lists_the_sub_verbs()
    {
        CommandLine.Parse(["config"]).Should().BeOfType<Request.Failed>().Which.Message.Should().Contain("get, set, reset");
        CommandLine.Parse(["config", "list"]).Should().BeOfType<Request.Failed>().Which.Message.Should().Contain("get, set, reset");
    }

    [Fact]
    public void Every_registered_command_is_in_the_help_text_and_its_example_parses()
    {
        // Derived from the register, not retyped: a command added there is checked here for free.
        CommandLine.Commands.Should().NotBeEmpty();
        foreach (var command in CommandLine.Commands)
        {
            CommandLine.HelpText.Should().Contain($"wsl-care {command.Usage}");
            command.Spellings.Should().NotBeEmpty();
            command.Spellings.Should().Contain(s => command.Example.Take(s.Count).SequenceEqual(s), $"the example of {command.Usage} must begin with one of its spellings");
            CommandLine.Parse(command.Example).Should().NotBeOfType<Request.Failed>($"the example of {command.Usage} must parse");
        }
    }

    [Fact]
    public void Help_mentions_the_three_configuration_layers()
    {
        CommandLine.HelpText.Should().Contain("three layers").And.Contain("config set").And.Contain("config get");
    }
}
