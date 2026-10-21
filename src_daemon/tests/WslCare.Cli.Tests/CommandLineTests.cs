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
    public void Status_takes_nothing_or_the_json_flag_and_refuses_anything_else()
    {
        CommandLine.Parse(["status"]).Should().Be(new Request.Status(Json: false));
        CommandLine.Parse(["status", "--json"]).Should().Be(new Request.Status(Json: true));
        CommandLine.Parse(["status", "--json", "--json"]).Should().BeOfType<Request.Failed>().Which.Message.Should().Contain("takes only --json");
        CommandLine.Parse(["status", "memory"]).Should().BeOfType<Request.Failed>().Which.Message.Should().Contain("\"memory\"");
    }

    [Fact]
    public void Preview_needs_all_takes_json_in_either_order_and_refuses_anything_else()
    {
        CommandLine.Parse(["preview", "--all"]).Should().Be(new Request.Preview(Json: false));
        CommandLine.Parse(["preview", "--all", "--json"]).Should().Be(new Request.Preview(Json: true));
        CommandLine.Parse(["preview", "--json", "--all"]).Should().Be(new Request.Preview(Json: true));
        CommandLine.Parse(["preview"]).Should().BeOfType<Request.Failed>().Which.Message.Should().Contain("needs --all").And.Contain("act <A#> --preview");
        CommandLine.Parse(["preview", "--json"]).Should().BeOfType<Request.Failed>();
        CommandLine.Parse(["preview", "--all", "A4"]).Should().BeOfType<Request.Failed>().Which.Message.Should().Contain("A4");
    }

    [Fact]
    public void Config_alone_or_with_an_unknown_sub_verb_lists_the_sub_verbs()
    {
        CommandLine.Parse(["config"]).Should().BeOfType<Request.Failed>().Which.Message.Should().Contain("get, set, reset");
        CommandLine.Parse(["config", "list"]).Should().BeOfType<Request.Failed>().Which.Message.Should().Contain("get, set, reset");
    }

    /// <summary>WR-D1: <c>tasks render</c> takes one task of the closed set and optionally <c>--json</c>; anything else is refused
    /// naming the two tasks.</summary>
    [Fact]
    public void Tasks_render_takes_one_task_of_the_closed_set_and_optionally_json()
    {
        CommandLine.Parse(["tasks", "render", "watch", "--json"]).Should().Be(new Request.TasksRender(Core.Scheduler.WindowsTask.Watch, true));
        CommandLine.Parse(["tasks", "render", "archive"]).Should().Be(new Request.TasksRender(Core.Scheduler.WindowsTask.Archive, false));
        foreach (var refused in new[] { new[] { "tasks", "render" }, ["tasks", "render", "collect"], ["tasks", "render", "Watch"], ["tasks", "render", "watch", "--xml"], ["tasks", "render", "watch", "archive"], ["tasks", "render", "--json", "watch"] })
        {
            CommandLine.Parse(refused).Should().BeOfType<Request.Failed>(string.Join(' ', refused)).Which.Message.Should().Contain("watch, archive");
        }

        CommandLine.Parse(["tasks"]).Should().BeOfType<Request.Failed>().Which.Message.Should().Contain("render");
    }

    /// <summary>WR-D2: <c>tasks stage</c> takes <c>--remove</c> and <c>--json</c>, each at most once, in any order; nothing else.</summary>
    [Fact]
    public void Tasks_stage_takes_remove_and_json_each_once_in_any_order()
    {
        CommandLine.Parse(["tasks", "stage"]).Should().Be(new Request.TasksStage(false, false));
        CommandLine.Parse(["tasks", "stage", "--json"]).Should().Be(new Request.TasksStage(false, true));
        CommandLine.Parse(["tasks", "stage", "--remove"]).Should().Be(new Request.TasksStage(true, false));
        CommandLine.Parse(["tasks", "stage", "--json", "--remove"]).Should().Be(new Request.TasksStage(true, true));
        foreach (var refused in new[] { new[] { "tasks", "stage", "--remove", "--remove" }, ["tasks", "stage", "watch"], ["tasks", "stage", "--force"], ["tasks", "stage", "--json", "--json"] })
        {
            CommandLine.Parse(refused).Should().BeOfType<Request.Failed>(string.Join(' ', refused)).Which.Message.Should().Contain("--remove");
        }
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
    public void The_longest_spelling_wins_when_two_commands_share_a_prefix_whatever_their_register_order()
    {
        // A synthetic register with the SHORT spelling first, so register order alone would pick it;
        // only the length ordering computed once by LongestFirst picks the long one.
        Command shortOne = new([["config"]], "config", "short", ["config"], _ => new Request.Help());
        Command longOne = new([["config", "get"]], "config get", "long", ["config", "get"], _ => new Request.Version());
        var spellings = CommandLine.LongestFirst([shortOne, longOne]);

        var (command, spelling) = CommandLine.Match(["config", "get", "x"], spellings);

        command.Should().BeSameAs(longOne);
        spelling.Should().Equal("config", "get");
        CommandLine.Match(["config", "list"], spellings).Command.Should().BeSameAs(shortOne, "a prefix only the short spelling matches still selects it");
        CommandLine.Match(["other"], spellings).Command.Should().BeNull();
    }

    [Fact]
    public void Help_mentions_the_three_configuration_layers()
    {
        CommandLine.HelpText.Should().Contain("three layers").And.Contain("config set").And.Contain("config get");
    }

    /// <summary>E19.S3: the worktree verbs take only <c>--json</c> — and the folders arrive on STDIN, never as a word on the command line.</summary>
    [Fact]
    public void The_worktrees_verbs_take_only_json_and_no_path()
    {
        CommandLine.Parse(["worktrees", "roots", "set"]).Should().Be(new Request.WorktreesRootsSet(false));
        CommandLine.Parse(["worktrees", "roots", "set", "--json"]).Should().Be(new Request.WorktreesRootsSet(true));
        CommandLine.Parse(["worktrees", "roots", "clear"]).Should().Be(new Request.WorktreesRootsClear(false));
        CommandLine.Parse(["worktrees", "roots", "clear", "--json"]).Should().Be(new Request.WorktreesRootsClear(true));
        CommandLine.Parse(["worktrees", "scan"]).Should().Be(new Request.WorktreesScan(false));
        CommandLine.Parse(["worktrees", "scan", "--json"]).Should().Be(new Request.WorktreesScan(true));
        foreach (var refused in new[] { new[] { "worktrees", "roots", "set", @"D:\work" }, ["worktrees", "roots", "set", "--json", "--json"], ["worktrees", "roots", "clear", "x"], ["worktrees", "scan", "--force"], ["worktrees", "scan", @"D:\work"] })
        {
            CommandLine.Parse(refused).Should().BeOfType<Request.Failed>($"\"{string.Join(' ', refused)}\" must be refused");
        }
    }

    [Fact]
    public void A_path_on_the_command_line_is_refused_saying_it_comes_on_stdin()
    {
        CommandLine.Parse(["worktrees", "roots", "set", @"D:\work"]).Should().BeOfType<Request.Failed>().Which.Message.Should().Contain("stdin");
    }

    [Fact]
    public void A_bare_worktrees_verb_names_what_it_needs()
    {
        CommandLine.Parse(["worktrees"]).Should().BeOfType<Request.Failed>().Which.Message.Should().Contain("roots").And.Contain("scan");
        CommandLine.Parse(["worktrees", "roots"]).Should().BeOfType<Request.Failed>().Which.Message.Should().Contain("set").And.Contain("clear");
    }
}
