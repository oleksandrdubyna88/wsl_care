using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Config;
using WslCare.Core.Mcp;

namespace WslCare.Core.Tests.Config;

/// <summary>
/// E14 S2c (the owner's Q-M2, 2026-10-08): users add their OWN MCP servers to the watched list by program file name —
/// <c>mcpServers.programs</c>, an OPEN list that is still never free text (plan §15q R1.3): every member a file name that is no AI
/// agent, no interpreter, shell or launcher (argv[0] of every script they run), not this product and not a catalogue server, at
/// most <see cref="McpUserPrograms.MaxMembers"/> of them. Each refusal says why.
/// </summary>
public sealed class McpProgramsKeyTests
{
    private static ValueCheck Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ConfigValidation.Check(ConfigKeys.McpServers.Programs, document.RootElement.Clone());
    }

    [Theory]
    [InlineData("claude", "an AI agent's program")]
    [InlineData("antigravity", "an AI agent's program")]
    [InlineData("node", "an interpreter, shell or launcher")]
    [InlineData("python3.12", "an interpreter, shell or launcher")]
    [InlineData("bash", "an interpreter, shell or launcher")]
    [InlineData("npx", "an interpreter, shell or launcher")]
    [InlineData("uvx", "an interpreter, shell or launcher")]
    [InlineData("init", "an interpreter, shell or launcher")]
    [InlineData("python3.13t", "an interpreter, shell or launcher")]
    [InlineData("python3.12d", "an interpreter, shell or launcher")]
    [InlineData("node-22", "an interpreter, shell or launcher")]
    [InlineData("perl5.36.0", "an interpreter, shell or launcher")]
    [InlineData("wsl-care", "this product")]
    [InlineData("coai-mcp", "mcpServers.watched")]
    public void An_agent_binary_an_interpreter_or_this_product_is_refused_as_a_program_naming_why(string program, string why)
    {
        var parsed = ConfigValidation.Parse(ConfigKeys.McpServers.Programs, $"creds-mcp,{program}");
        var fromJson = Json($"[\"creds-mcp\", \"{program}\"]");

        parsed.Should().BeOfType<ValueCheck.Invalid>().Which.Message.Should().Contain("mcpServers.programs").And.Contain($"\"{program}\"").And.Contain(why);
        fromJson.Should().BeOfType<ValueCheck.Invalid>().Which.Message.Should().Contain(why);
    }

    [Theory]
    [InlineData("/home/me/.local/bin/creds-mcp")]
    [InlineData("bin/creds-mcp")]
    [InlineData("..")]
    [InlineData(".hidden")]
    [InlineData("creds mcp")]
    [InlineData("creds-mcp;rm")]
    [InlineData("creds-mcp.exe")]
    [InlineData("creds-mcp\n")]
    [InlineData("a12345678901234567890123456789012345678901234567890123456789012345")]
    public void A_program_list_past_its_cap_or_with_a_path_is_refused(string program)
    {
        var tooMany = string.Join(",", Enumerable.Range(1, McpUserPrograms.MaxMembers + 1).Select(i => $"server{i}"));

        // A layer's JSON value, as written (the command line trims each member, so "x\n" there is "x").
        Json(new System.Text.Json.Nodes.JsonArray(System.Text.Json.Nodes.JsonValue.Create(program)).ToJsonString()).Should().BeOfType<ValueCheck.Invalid>()
            .Which.Message.Should().Contain($"\"{program}\"").And.Contain(program.EndsWith(".exe", StringComparison.Ordinal) ? McpUserPrograms.WithExe : McpUserPrograms.NotAFileName);
        ConfigValidation.Parse(ConfigKeys.McpServers.Programs, tooMany).Should().BeOfType<ValueCheck.Invalid>()
            .Which.Message.Should().Contain("at most 32");
    }

    [Fact]
    public void A_list_of_program_file_names_is_accepted_and_the_default_is_empty()
    {
        var atTheCap = Enumerable.Range(1, McpUserPrograms.MaxMembers).Select(i => $"server{i}").ToList();

        ConfigValidation.Parse(ConfigKeys.McpServers.Programs, "creds-mcp, my_server.v2").Should().BeOfType<ValueCheck.Ok>()
            .Which.Value.Should().Be(new ConfigValue.TextList(["creds-mcp", "my_server.v2"]));
        ConfigValidation.Parse(ConfigKeys.McpServers.Programs, string.Join(",", atTheCap)).Should().BeOfType<ValueCheck.Ok>();
        Json("[]").Should().BeOfType<ValueCheck.Ok>();
        ConfigLoader.Load([(ConfigLoader.DefaultsFile, new Core.Files.FileReadResult.Content(ConfigLoader.EmbeddedDefaults()))]).Config.TextList(ConfigKeys.McpServers.Programs).Should().BeEmpty("no program is the user's until they add it");
    }

    /// <summary>coai code round 2026-10-08, finding 1: a closed list given a member rule and a cap as well must still hold every
    /// member to its allowed set — the rule narrows, it never replaces the catalogue.</summary>
    [Fact]
    public void A_closed_list_with_a_member_rule_still_refuses_what_its_catalogue_does_not_hold()
    {
        var key = new ConfigKey.TextListKey("test.closed", ["creds-mcp"], new TextRule.McpProgramName(), 4);

        ConfigValidation.Parse(key, "creds-mcp").Should().BeOfType<ValueCheck.Ok>();
        ConfigValidation.Parse(key, "other-mcp").Should().BeOfType<ValueCheck.Invalid>().Which.Message.Should().Contain("\"other-mcp\"");
    }

    /// <summary>Own code review 2026-10-08, finding 3: a name this build's catalogues refuse — an agent, launcher or server a later
    /// release ADDS — must not turn a layer written for an older build into a configuration error, which makes the whole run
    /// observe-only: the member is left out with a notice. A malformed member is still an error, and <c>config set</c> still
    /// refuses the name.</summary>
    [Fact]
    public void A_layer_listing_a_name_a_newer_catalogue_refuses_still_loads_leaving_it_out_with_a_notice()
    {
        var user = new ConfigLayerFile(ConfigLayer.User, "/home/me/.config/wsl-care/config.json");
        (ConfigLayerFile, Core.Files.FileReadResult) Layer(string json) => (user, new Core.Files.FileReadResult.Content(System.Text.Encoding.UTF8.GetBytes(json)));
        var defaults = (ConfigLoader.DefaultsFile, (Core.Files.FileReadResult)new Core.Files.FileReadResult.Content(ConfigLoader.EmbeddedDefaults()));

        var outdated = ConfigLoader.Load([defaults, Layer("{ \"mcpServers\": { \"programs\": [\"creds-mcp\", \"claude\", \"bunx\"] } }")]);
        var malformed = ConfigLoader.Load([defaults, Layer("{ \"mcpServers\": { \"programs\": [\"creds-mcp\", \"/bin/x\"] } }")]);

        outdated.IsObserveOnly.Should().BeFalse("an upgrade's longer refusal list must not stop every action");
        outdated.Config.TextList(ConfigKeys.McpServers.Programs).Should().Equal("creds-mcp");
        outdated.Notices.Where(n => n.Key == "mcpServers.programs").Select(n => n.Message).Should().HaveCount(2)
            .And.Contain(m => m.Contains("\"claude\"") && m.Contains(McpUserPrograms.AnAgent))
            .And.Contain(m => m.Contains("\"bunx\"") && m.Contains(McpUserPrograms.ALauncher));
        malformed.IsObserveOnly.Should().BeTrue("a member that is no file name is a broken layer, as before");
        ConfigValidation.Parse(ConfigKeys.McpServers.Programs, "claude").Should().BeOfType<ValueCheck.Invalid>("config set still refuses it");
    }

    [Theory]
    [InlineData("bunx")]
    [InlineData("pnpx")]
    [InlineData("pwsh")]
    [InlineData("lua5.4")]
    [InlineData("tini")]
    [InlineData("dumb-init")]
    public void The_npx_style_runners_other_shells_and_init_wrappers_are_launchers_too(string program)
    {
        ConfigValidation.Parse(ConfigKeys.McpServers.Programs, program).Should().BeOfType<ValueCheck.Invalid>()
            .Which.Message.Should().Contain(McpUserPrograms.ALauncher);
    }

    /// <summary>A name that merely begins like a launcher is still a program of its own.</summary>
    [Fact]
    public void A_name_that_only_begins_like_a_launcher_is_accepted()
    {
        ConfigValidation.Parse(ConfigKeys.McpServers.Programs, "go2mcp,nodemcp,python-mcp,bashful").Should().BeOfType<ValueCheck.Ok>();
    }

    [Fact]
    public void A_closed_list_keeps_refusing_what_its_catalogue_does_not_hold()
    {
        ConfigValidation.Parse(ConfigKeys.McpServers.Watched, "creds-mcp").Should().BeOfType<ValueCheck.Invalid>()
            .Which.Message.Should().Be("mcpServers.watched must be a list of: coai-mcp (comma-separated on the command line); got \"creds-mcp\"");
    }
}
