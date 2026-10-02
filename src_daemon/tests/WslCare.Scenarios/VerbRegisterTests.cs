using FluentAssertions;

using WslCare.Cli;

namespace WslCare.Scenarios;

/// <summary>
/// The register is DERIVED: the verbs come from <see cref="CommandLine.Commands"/>, never from a list
/// typed here, and each one is run against the built CLI and looked up in the flow catalogue.
/// </summary>
public sealed class VerbRegisterTests
{
    private const string ModuleTestsKey = "WslCare.ModuleTests";

    public static TheoryData<string> RegisteredUsages => [.. CommandLine.Commands.Select(c => c.Usage)];

    [Theory]
    [MemberData(nameof(RegisteredUsages))]
    public async Task Every_registered_verb_runs_its_example_against_the_built_cli_without_crashing(string usage)
    {
        var command = CommandLine.Commands.Single(c => c.Usage == usage);
        using var home = new ScenarioHome("register");

        var result = await home.RunAsync(command.Example);

        // 0, or the usage refusal: never the internal-error code, never an interruption, never a .NET crash.
        result.Exit.Should().BeOneOf([(int)ExitCode.Ok, (int)ExitCode.Usage], $"\"wsl-care {string.Join(' ', command.Example)}\" is the example of {usage}; stderr: {result.Stderr}");
        result.Stderr.Should().NotContain("internal error").And.NotContain("Unhandled exception");
    }

    [Fact]
    public void Every_registered_verb_has_a_row_in_the_flow_catalogue_of_module_tests()
    {
        var catalogue = File.ReadAllText(ScenarioHome.Stamped(ModuleTestsKey));

        var missing = VerbRegister.MissingFrom(catalogue, CommandLine.Commands.Select(c => c.Usage));

        missing.Should().BeEmpty("every verb in CommandLine.Commands needs a flow-catalogue row in research/module_tests.md whose first cell starts with `wsl-care <usage>`; missing: {0}", string.Join("; ", missing));
    }

    [Fact]
    public void The_register_check_names_a_planted_verb_that_has_no_row()
    {
        // The companion: without it, a check that never finds anything missing passes forever.
        var catalogue = File.ReadAllText(ScenarioHome.Stamped(ModuleTestsKey));
        const string planted = "planted-verb-without-a-row <thing>";

        var missing = VerbRegister.MissingFrom(catalogue, [.. CommandLine.Commands.Select(c => c.Usage), planted]);

        missing.Should().Equal(planted);
    }

    [Fact]
    public void The_register_check_counts_only_rows_of_the_flow_catalogue_not_prose_or_other_tables()
    {
        const string markdown = """
            # module_tests

            The harness runs `wsl-care config get [key] [--json]` in prose.

            | Guarantee | Test file |
            |---|---|
            | `wsl-care config get [key] [--json]` in another table | `X.cs` |

            ## Flow catalogue

            | Flow | Covered | By |
            |---|---|---|
            | `wsl-care --help` (and `-h`) | covered | `HelpAndVersionFlows` |

            ## What it does not prove

            | `wsl-care config set <key> <value>` | after the catalogue | `Y` |
            """;

        var missing = VerbRegister.MissingFrom(markdown, ["--help", "config get [key] [--json]", "config set <key> <value>"]);

        missing.Should().Equal("config get [key] [--json]", "config set <key> <value>");
    }
}
