using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Config;

namespace WslCare.Core.Tests.Config;

/// <summary>
/// Plan §15q R1.3 (review B1): no configuration key may widen what root does beyond the closed registry. A free text or list key
/// is the hole — <c>processes.families</c> accepted <c>other</c>, the catch-all family, so root's A11 would end the idle orphaned
/// processes of every account but root's. Every text and list key is CLOSED (an allowed set) or a declared, validated shape.
/// </summary>
public sealed class ConfigKeyClosureTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Theory]
    [InlineData("other")]
    [InlineData("ai-agents")]
    [InlineData("testhost,other")]
    [InlineData("anything-at-all")]
    public void A_families_list_cannot_widen_A11_beyond_the_named_families(string families)
    {
        ConfigValidation.Parse(ConfigKeys.Processes.Families, families).Should().BeOfType<ValueCheck.Invalid>()
            .Which.Message.Should().Contain("processes.families");
        var json = "[" + string.Join(",", families.Split(',').Select(f => $"\"{f}\"")) + "]";
        ConfigValidation.Check(ConfigKeys.Processes.Families, Json(json)).Should().BeOfType<ValueCheck.Invalid>();
    }

    [Fact]
    public void The_named_families_are_still_accepted()
    {
        ConfigValidation.Parse(ConfigKeys.Processes.Families, "dotnet-build-servers, testhost, node").Should().BeOfType<ValueCheck.Ok>()
            .Which.Value.Should().Be(new ConfigValue.TextList(["dotnet-build-servers", "testhost", "node"]));
        ConfigValidation.Check(ConfigKeys.Processes.Families, Json("[]")).Should().BeOfType<ValueCheck.Ok>();
    }
}
