using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Agents;
using WslCare.Core.Config;

namespace WslCare.Core.Tests.Agents;

/// <summary>
/// The SHAPE of <c>aiAgents.extra</c> (plan §15q R2.1), the fifth key shape: every refusal names its entry and its rule, at
/// <c>config set</c> and at load alike — before anything looks at the disk.
/// </summary>
public sealed class ExtraAgentShapeTests
{
    private const string Valid = """{ "cli": "/home/me/.local/bin/mycli", "side": "wsl", "name": "My CLI", "dataFolders": ["/home/me/.mycli"], "sessionGlob": "sessions/**" }""";

    private static ValueCheck Check(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ConfigValidation.Check(ConfigKeys.AiAgents.Extra, document.RootElement);
    }

    [Fact]
    public void A_valid_list_is_taken_with_every_member()
    {
        var ok = Check($"[{Valid}]").Should().BeOfType<ValueCheck.Ok>().Subject;

        var agent = ok.Value.Should().BeOfType<ConfigValue.AgentList>().Subject.Agents.Should().ContainSingle().Subject;
        agent.Should().Be(new ExtraAgent("/home/me/.local/bin/mycli", "wsl", "My CLI", ["/home/me/.mycli"], "sessions/**"));
        agent.Id.Should().Be("manual:My CLI");
    }

    [Fact]
    public void The_default_is_an_empty_list_and_a_windows_entry_takes_a_drive_path()
    {
        Check("[]").Should().BeOfType<ValueCheck.Ok>();
        Check("""[{ "cli": "C:\\tools\\x.exe", "side": "windows", "name": "x", "dataFolders": ["C:\\Users\\me\\.x"], "sessionGlob": "" }]""").Should().BeOfType<ValueCheck.Ok>();
    }

    [Theory]
    [InlineData("""{ "cli": "/a", "side": "wsl", "name": "x", "dataFolders": ["relative/folder"], "sessionGlob": "" }""", "must be an absolute path")]
    [InlineData("""{ "cli": "/a", "side": "wsl", "name": "x", "dataFolders": ["/home/me/../../etc"], "sessionGlob": "" }""", ". or .. segment")]
    [InlineData("""{ "cli": "/a", "side": "wsl", "name": "x", "dataFolders": ["-rf"], "sessionGlob": "" }""", "starts with '-'")]
    [InlineData("""{ "cli": "/a", "side": "wsl", "name": "x", "dataFolders": ["/home/me/x\u0007"], "sessionGlob": "" }""", "control character")]
    [InlineData("""{ "cli": "/a", "side": "wsl", "name": "x", "dataFolders": [], "sessionGlob": "" }""", "1 to 8 folders")]
    [InlineData("""{ "cli": "/a", "side": "wsl", "name": "x", "dataFolders": ["/1","/2","/3","/4","/5","/6","/7","/8","/9"], "sessionGlob": "" }""", "1 to 8 folders")]
    [InlineData("""{ "cli": "relative", "side": "wsl", "name": "x", "dataFolders": ["/home/me/.x"], "sessionGlob": "" }""", "cli must be an absolute path")]
    [InlineData("""{ "cli": "/a", "side": "mac", "name": "x", "dataFolders": ["/home/me/.x"], "sessionGlob": "" }""", "side must be")]
    [InlineData("""{ "cli": "/a", "side": "wsl", "name": "-x", "dataFolders": ["/home/me/.x"], "sessionGlob": "" }""", "name must be")]
    [InlineData("""{ "cli": "/a", "side": "wsl", "name": "x\n", "dataFolders": ["/home/me/.x"], "sessionGlob": "" }""", "name must be")]
    [InlineData("""{ "cli": "/a", "side": "wsl", "name": "x", "dataFolders": ["/home/me/.x"], "sessionGlob": "../escape/*" }""", "sessionGlob must be")]
    [InlineData("""{ "cli": "/a", "side": "wsl", "name": "x", "dataFolders": ["/home/me/.x"], "sessionGlob": "/abs/*" }""", "sessionGlob must be")]
    [InlineData("""{ "cli": "/a", "side": "wsl", "name": "x", "dataFolders": ["/home/me/.x"], "sessionGlob": "a/[b]" }""", "sessionGlob must be")]
    [InlineData("""{ "cli": "/a", "side": "wsl", "name": "x", "dataFolders": ["/home/me/.x"], "sessionGlob": "", "archive": {} }""", "unknown member")]
    [InlineData("""{ "cli": "/a", "side": "wsl", "name": "x", "dataFolders": "/home/me/.x", "sessionGlob": "" }""", "dataFolders a list of text")]
    [InlineData("""{ "cli": "/a", "side": "windows", "name": "x", "dataFolders": ["/home/me/.x"], "sessionGlob": "" }""", "X:\\")]
    public void Each_shape_rule_refuses_naming_the_entry_and_the_rule(string entry, string rule) =>
        Check($"[{entry}]").Should().BeOfType<ValueCheck.Invalid>().Which.Message.Should().Contain("aiAgents.extra").And.Contain("entry 1").And.Contain(rule);

    [Fact]
    public void At_most_sixteen_entries_and_no_name_twice()
    {
        var seventeen = string.Join(",", Enumerable.Range(1, 17).Select(i => Valid.Replace("My CLI", $"cli {i}", StringComparison.Ordinal)));
        Check($"[{seventeen}]").Should().BeOfType<ValueCheck.Invalid>().Which.Message.Should().Contain("at most 16");
        Check($"[{Valid},{Valid}]").Should().BeOfType<ValueCheck.Invalid>().Which.Message.Should().Contain("used twice");
    }

    [Fact]
    public void A_text_that_is_not_json_and_a_value_that_is_not_a_list_are_refused()
    {
        ConfigValidation.Parse(ConfigKeys.AiAgents.Extra, "not json").Should().BeOfType<ValueCheck.Invalid>().Which.Message.Should().Contain("not JSON");
        Check(Valid).Should().BeOfType<ValueCheck.Invalid>().Which.Message.Should().Contain("must be a JSON list");
    }

    [Fact]
    public void A_list_written_and_read_back_is_the_same_list()
    {
        var ok = (ValueCheck.Ok)Check($"[{Valid}]");

        var back = ConfigValidation.Check(ConfigKeys.AiAgents.Extra, ok.Value.ToJsonElement()).Should().BeOfType<ValueCheck.Ok>().Subject;

        back.Value.Should().Be(ok.Value);
    }
}
