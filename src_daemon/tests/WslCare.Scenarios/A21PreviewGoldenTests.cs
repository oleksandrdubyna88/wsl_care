using System.Text.Json.Nodes;

using FluentAssertions;

namespace WslCare.Scenarios;

/// <summary>S7b.2b: the A21 line the extension replays selects what its producer staged — on every leg, so a producer that stops
/// selecting (an anchor that drifted, a history that is not idle) fails here rather than as an empty golden on Linux.</summary>
public sealed class A21PreviewGoldenTests
{
    [Fact]
    public async Task The_golden_a21_line_selects_both_staged_relays_by_pid_and_start()
    {
        var line = JsonNode.Parse(await A21PreviewGolden.TextAsync(new HashSet<string>(StringComparer.Ordinal)))!;

        line["id"]!.GetValue<string>().Should().Be("A21");
        line["status"]!.GetValue<string>().Should().Be("previewed");
        line["preview"]!["count"]!.GetValue<int>().Should().Be(2, line.ToJsonString());
        line["shown"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("12062:48563", "12063:48564");
        line["preview"]!["items"]!.AsArray().Should().HaveCount(2);
        line["shownTruncated"].Should().BeNull("two relays are far under the cap");
    }
}
