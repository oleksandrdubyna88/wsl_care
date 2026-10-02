using System.Text;
using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Config;

namespace WslCare.Core.Tests.Config;

/// <summary>Reading one layer: nested objects become dotted keys, and every leaf knows its line.</summary>
public sealed class ConfigDocumentTests
{
    private static ConfigDocumentResult Parse(string json) => ConfigDocument.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Nested_objects_flatten_to_dotted_keys_and_arrays_stay_leaves()
    {
        var parsed = Parse("""
            {
              "dryRun": false,
              "volumes": { "anonymousMaxGb": 25, "anonymousMaxCount": 10 },
              "processes": { "families": ["a", "b"] }
            }
            """).Should().BeOfType<ConfigDocumentResult.Parsed>().Subject;

        parsed.Entries.Select(e => e.Key).Should().Equal("dryRun", "volumes.anonymousMaxGb", "volumes.anonymousMaxCount", "processes.families");
        parsed.Entries.Single(e => e.Key == "processes.families").Value.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public void Every_leaf_carries_the_line_its_key_is_written_on()
    {
        var parsed = (ConfigDocumentResult.Parsed)Parse("{\n  \"dryRun\": false,\n  \"volumes\": {\n    \"anonymousMaxGb\": 25,\n\n    \"anonymousMaxCount\": 10\n  },\n  \"processes\": { \"families\": [\"a\",\n \"b\"] },\n  \"distro\": \"Ubuntu\"\n}");

        parsed.Entries.Should().BeEquivalentTo(
        [
            new { Key = "dryRun", Line = 2 },
            new { Key = "volumes.anonymousMaxGb", Line = 4 },
            new { Key = "volumes.anonymousMaxCount", Line = 6 },
            new { Key = "processes.families", Line = 8 },
            new { Key = "distro", Line = 10 },
        ]);
    }

    [Fact]
    public void A_syntax_error_reports_its_line_in_one_sentence()
    {
        var malformed = Parse("{\n  \"dryRun\": false,\n  \"volumes\": { \"anonymousMaxGb\": 25,\n}").Should().BeOfType<ConfigDocumentResult.Malformed>().Subject;

        malformed.Line.Should().Be(4);
        malformed.Message.Should().NotContain("LineNumber").And.NotContain("BytePositionInLine").And.NotBeEmpty();
    }

    [Fact]
    public void A_document_that_is_not_an_object_is_malformed()
    {
        Parse("[1, 2]").Should().BeOfType<ConfigDocumentResult.Malformed>().Which.Message.Should().Contain("JSON object");
        Parse("\"text\"").Should().BeOfType<ConfigDocumentResult.Malformed>();
    }

    [Fact]
    public void Comments_trailing_commas_a_bom_and_a_schema_member_are_accepted()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("{\n  // a comment\n  \"$schema\": \"./x.json\",\n  \"dryRun\": true,\n}")).ToArray();

        var parsed = ConfigDocument.Parse(bytes).Should().BeOfType<ConfigDocumentResult.Parsed>().Subject;

        parsed.Entries.Should().ContainSingle().Which.Key.Should().Be("dryRun");
    }

    [Fact]
    public void An_empty_object_has_no_leaves()
    {
        Parse("{}").Should().BeOfType<ConfigDocumentResult.Parsed>().Which.Entries.Should().BeEmpty();
    }
}
