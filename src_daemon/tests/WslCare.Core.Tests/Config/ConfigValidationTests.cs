using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Config;

namespace WslCare.Core.Tests.Config;

/// <summary>One validator for both roads in — a file's JSON and the command line's text.</summary>
public sealed class ConfigValidationTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Theory]
    [InlineData("true", true)]
    [InlineData("FALSE", false)]
    public void A_bool_from_the_command_line_is_case_insensitive(string text, bool expected)
    {
        ConfigValidation.Parse(ConfigKeys.DryRun, text).Should().BeOfType<ValueCheck.Ok>()
            .Which.Value.Should().Be(new ConfigValue.Bool(expected));
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("")]
    public void A_non_bool_for_a_bool_key_is_refused_naming_the_key_and_the_legal_values(string text)
    {
        ConfigValidation.Parse(ConfigKeys.DryRun, text).Should().BeOfType<ValueCheck.Invalid>()
            .Which.Message.Should().Contain("dryRun").And.Contain("true or false");
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("100000", 100000)]
    [InlineData("25", 25)]
    public void An_int_inside_its_range_is_accepted(string text, int expected)
    {
        ConfigValidation.Parse(ConfigKeys.Volumes.AnonymousMaxGb, text).Should().BeOfType<ValueCheck.Ok>()
            .Which.Value.Should().Be(new ConfigValue.Int(expected));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("100001")]
    [InlineData("2.5")]
    [InlineData("many")]
    public void An_int_outside_its_range_or_not_a_whole_number_is_refused_with_the_range(string text)
    {
        ConfigValidation.Parse(ConfigKeys.Volumes.AnonymousMaxGb, text).Should().BeOfType<ValueCheck.Invalid>()
            .Which.Message.Should().Contain("volumes.anonymousMaxGb").And.Contain("from 0 to 100000");
    }

    [Fact]
    public void An_enumerated_text_key_accepts_only_its_values_exactly()
    {
        ConfigValidation.Parse(ConfigKeys.Logging.MinimumLevel, "Warning").Should().BeOfType<ValueCheck.Ok>();
        ConfigValidation.Parse(ConfigKeys.Logging.MinimumLevel, "warning").Should().BeOfType<ValueCheck.Invalid>()
            .Which.Message.Should().Contain("one of: Verbose, Debug, Information, Warning, Error, Fatal");
    }

    [Fact]
    public void A_free_text_key_accepts_anything_including_empty()
    {
        ConfigValidation.Parse(ConfigKeys.Archive.BaseFolder, "").Should().BeOfType<ValueCheck.Ok>();
        ConfigValidation.Parse(ConfigKeys.Distro, "Ubuntu-26.04").Should().BeOfType<ValueCheck.Ok>();
    }

    [Fact]
    public void A_list_from_the_command_line_is_comma_separated_and_trimmed()
    {
        ConfigValidation.Parse(ConfigKeys.Processes.Families, " node , testhost,,").Should().BeOfType<ValueCheck.Ok>()
            .Which.Value.Should().Be(new ConfigValue.TextList(["node", "testhost"]));
    }

    [Fact]
    public void Json_values_are_checked_by_shape_with_the_offending_value_named()
    {
        ConfigValidation.Check(ConfigKeys.DryRun, Json("\"true\"")).Should().BeOfType<ValueCheck.Invalid>()
            .Which.Message.Should().Contain("the text \"true\"");
        ConfigValidation.Check(ConfigKeys.Volumes.AnonymousMaxGb, Json("\"20\"")).Should().BeOfType<ValueCheck.Invalid>();
        ConfigValidation.Check(ConfigKeys.Volumes.AnonymousMaxGb, Json("20.5")).Should().BeOfType<ValueCheck.Invalid>();
        ConfigValidation.Check(ConfigKeys.Volumes.AnonymousMaxGb, Json("999999999")).Should().BeOfType<ValueCheck.Invalid>()
            .Which.Message.Should().Contain("got 999999999");
        ConfigValidation.Check(ConfigKeys.Processes.Families, Json("[\"a\", 1]")).Should().BeOfType<ValueCheck.Invalid>();
        ConfigValidation.Check(ConfigKeys.Processes.Families, Json("[\"a\", \"b\"]")).Should().BeOfType<ValueCheck.Ok>();
        ConfigValidation.Check(ConfigKeys.Distro, Json("{}")).Should().BeOfType<ValueCheck.Invalid>().Which.Message.Should().Contain("an object");
    }
}
