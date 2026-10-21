using System.Text.Json;
using System.Text.Json.Nodes;

using FluentAssertions;

using WslCare.Core.Collect;
using WslCare.Core.Collectors;
using WslCare.Core.Health;
using WslCare.Core.Json;
using WslCare.Core.Thresholds;

namespace WslCare.Core.Tests.Collect;

/// <summary>
/// E19.S1: the <c>disk.lettered</c> list of the run detail and of <c>status --json</c> — the per-volume judgements of
/// <c>disk.volumeFree</c> in their wire shape, so the extension never parses the verdict's value text. A figure the volume did not
/// give is OMITTED (never 0) and its reason rides in <c>judgedBy</c>.
/// </summary>
public sealed class DiskReportLetteredTests
{
    private const long Gib = 1L << 30;

    private static VolumeJudgement Critical() =>
        new("D", Reading.Of(195 * Gib), Reading.Of((long)(4.1 * Gib)), Level.Critical, "gib-or-percent", true);

    private static VolumeJudgement Unread() =>
        new("F", Reading.Of(195 * Gib), Reading.Missing<long>("Get-Volume gave no free space"), Level.Unknown, "not judged: the free space was not read (Get-Volume gave no free space)", true);

    [Fact]
    public void A_judgement_becomes_the_letter_the_bytes_the_percent_the_level_and_the_rule()
    {
        var lettered = DiskReports.Lettered([Critical()]);

        lettered.Should().ContainSingle().Which.Should().Be(new LetteredVolumeReport("D", 195 * Gib, (long)(4.1 * Gib), 2.1, Level.Critical, "gib-or-percent"));
    }

    [Fact]
    public void An_unread_figure_is_omitted_never_zero_and_its_reason_rides_in_judged_by()
    {
        var lettered = DiskReports.Lettered([Unread()]).Single();

        lettered.FreeBytes.Should().BeNull();
        lettered.FreePercent.Should().BeNull();
        lettered.SizeBytes.Should().Be(195 * Gib);
        lettered.Level.Should().Be(Level.Unknown);
        lettered.JudgedBy.Should().Contain("Get-Volume gave no free space");
    }

    [Fact]
    public void The_wire_spelling_is_camel_case_with_a_lowercase_level_and_no_null_members()
    {
        var report = DiskReports.Unavailable("no virtual disk was read") with { Lettered = DiskReports.Lettered([Critical(), Unread()]) };

        var json = JsonNode.Parse(JsonSerializer.Serialize(report, WslCareJsonContext.Default.DiskReport))!;

        var lettered = json["lettered"]!.AsArray();
        lettered.Should().HaveCount(2);
        lettered[0]!.ToJsonString().Should().Be("""{"letter":"D","sizeBytes":209379655680,"freeBytes":4402341478,"freePercent":2.1,"level":"critical","judgedBy":"gib-or-percent"}""");
        lettered[1]!.AsObject().ContainsKey("freeBytes").Should().BeFalse("an unread free space is omitted, not 0");
        lettered[1]!["level"]!.GetValue<string>().Should().Be("unknown");
    }

    [Fact]
    public void A_disk_section_without_lettered_stays_without_it_so_an_older_detail_reads_as_not_recorded()
    {
        var json = JsonSerializer.Serialize(DiskReports.Unavailable("x"), WslCareJsonContext.Default.DiskReport);

        json.Should().NotContain("lettered");
        JsonSerializer.Deserialize(json, WslCareJsonContext.Default.DiskReport)!.Lettered.Should().BeNull();
    }

    [Fact]
    public void The_lettered_list_round_trips_through_the_detail_s_json()
    {
        var report = DiskReports.Unavailable("x") with { Lettered = DiskReports.Lettered([Critical(), Unread()]) };

        var back = JsonSerializer.Deserialize(JsonSerializer.Serialize(report, WslCareJsonContext.Default.DiskReport), WslCareJsonContext.Default.DiskReport)!;

        back.Lettered.Should().Equal(report.Lettered);
    }

    [Fact]
    public void Every_judgement_is_listed_the_not_judged_ones_too_so_a_reader_sees_why_a_volume_is_absent_from_the_verdict()
    {
        var stick = new VolumeJudgement("E", Reading.Of(31_170_560L), Reading.Of(0L), Level.Unknown, "not judged: smaller than 8 GiB (disk.volumeFreeMinSizeGib)", false);

        var lettered = DiskReports.Lettered([Critical(), stick]);

        lettered.Select(l => l.Letter).Should().Equal("D", "E");
        lettered[1].JudgedBy.Should().StartWith("not judged: ");
    }
}
