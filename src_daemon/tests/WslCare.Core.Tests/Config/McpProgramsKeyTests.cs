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

    [Fact]
    public void A_closed_list_keeps_refusing_what_its_catalogue_does_not_hold()
    {
        ConfigValidation.Parse(ConfigKeys.McpServers.Watched, "creds-mcp").Should().BeOfType<ValueCheck.Invalid>()
            .Which.Message.Should().Be("mcpServers.watched must be a list of: coai-mcp (comma-separated on the command line); got \"creds-mcp\"");
    }
}
