using System.Text;
using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Config;

namespace WslCare.Core.Tests.Config;

/// <summary>
/// The register and the embedded defaults are two spellings of one list; this holds them together
/// in both directions, and holds the register to the plan's shapes.
/// </summary>
public sealed class ConfigSchemaTests
{
    [Fact]
    public void Every_key_in_the_register_has_a_default_and_every_default_is_in_the_register()
    {
        var parsed = ConfigDocument.Parse(ConfigLoader.EmbeddedDefaults()).Should().BeOfType<ConfigDocumentResult.Parsed>().Subject;

        parsed.Entries.Select(e => e.Key).Should().BeEquivalentTo(ConfigKeys.All.Select(k => k.Name));
    }

    [Fact]
    public void Every_default_validates_against_its_key()
    {
        var parsed = (ConfigDocumentResult.Parsed)ConfigDocument.Parse(ConfigLoader.EmbeddedDefaults());

        foreach (var entry in parsed.Entries)
        {
            var key = ConfigKeys.Find(entry.Key);
            key.Should().NotBeNull();
            ConfigValidation.Check(key!, entry.Value).Should().BeOfType<ValueCheck.Ok>($"the default of {entry.Key} must be valid");
        }
    }

    [Fact]
    public void Key_names_are_unique_dotted_camel_case()
    {
        ConfigKeys.All.Select(k => k.Name).Should().OnlyHaveUniqueItems();
        ConfigKeys.All.Should().OnlyContain(k => k.Name.Split('.').All(part => part.Length > 0 && char.IsAsciiLetterLower(part[0]) || part.StartsWith("A")));
    }

    [Fact]
    public void The_plan_defaults_are_the_shipped_defaults()
    {
        var config = ConfigLoader.Load([(ConfigLoader.DefaultsFile, new WslCare.Core.Files.FileReadResult.Content(ConfigLoader.EmbeddedDefaults()))]).Config;

        config.Bool(ConfigKeys.DryRun).Should().BeTrue("plan §5: dryRun for the first 7 days of the timer");
        config.Int(ConfigKeys.Volumes.AnonymousMaxGb).Should().Be(20);
        config.Int(ConfigKeys.Containers.StoppedOlderThanDays).Should().Be(7);
        config.Bool(ConfigKeys.Auto.A5).Should().BeFalse("plan §5 A5: other containers off by default");
        config.Bool(ConfigKeys.Auto.A5Testcontainers).Should().BeTrue();
        config.Bool(ConfigKeys.Auto.A11).Should().BeTrue("E14 S3b: the owner switched A11 on by default (Q15, 2026-10-09)");
        config.TextList(ConfigKeys.Processes.Families).Should().Equal(
            ["dotnet-build-servers", "testhost", "language-servers"],
            "E14 S3b: the C# language server joins A11's defaults (Q14); vscode-server never does, it matches the daemonised VS Code server");
        config.Text(ConfigKeys.Logging.MinimumLevel).Should().Be("Information");
        config.Int(ConfigKeys.Logging.RetentionDays).Should().Be(14);
    }

    [Fact]
    public void A_config_value_round_trips_through_json()
    {
        var list = new ConfigValue.TextList(["a", "b"]);

        var element = list.ToJsonElement();

        element.ValueKind.Should().Be(JsonValueKind.Array);
        element.EnumerateArray().Select(e => e.GetString()).Should().Equal("a", "b");
        new ConfigValue.Int(7).ToJsonElement().GetInt32().Should().Be(7);
        new ConfigValue.Bool(true).ToJsonElement().GetBoolean().Should().BeTrue();
        Encoding.UTF8.GetString(ConfigLoader.EmbeddedDefaults()).Should().StartWith("{");
    }
}
