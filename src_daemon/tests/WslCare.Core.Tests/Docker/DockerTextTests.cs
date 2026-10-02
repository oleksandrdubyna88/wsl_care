using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Docker;

namespace WslCare.Core.Tests.Docker;

/// <summary>The docker CLI's human spellings read back: sizes in BOTH bases, both timestamp shapes, labels.</summary>
public sealed class DockerTextTests
{
    [Theory]
    [InlineData("988MB", 988_000_000L)]
    [InlineData("19.71kB", 19_710L)]
    [InlineData("0B", 0L)]
    [InlineData("1.007GB", 1_007_000_000L)]
    [InlineData("9.806GB (48%)", 9_806_000_000L)]
    [InlineData("20.5kB (virtual 306MB)", 20_500L)]
    [InlineData("53.84MiB", 56_455_332L)]
    [InlineData("44.89GiB", 48_200_270_479L)]
    public void A_size_is_read_in_the_base_its_suffix_names(string text, long bytes) =>
        DockerText.Bytes(text).Should().Be(Reading.Of(bytes));

    [Fact]
    public void Megabytes_and_mebibytes_are_not_the_same_figure() =>
        DockerText.Bytes("100MiB").ValueOr(0).Should().Be(104_857_600).And.NotBe(DockerText.Bytes("100MB").ValueOr(0));

    [Theory]
    [InlineData("N/A")]
    [InlineData("")]
    [InlineData("12 parsecs")]
    [InlineData("MB")]
    public void Something_that_is_not_a_size_is_unavailable_never_zero(string text) =>
        DockerText.Bytes(text).IsAvailable.Should().BeFalse();

    [Theory]
    [InlineData("2026-10-02 14:23:39 +0200 CEST", "2026-10-02T12:23:39Z")]
    [InlineData("2024-12-03 10:16:44 +0100 CET", "2024-12-03T09:16:44Z")]
    [InlineData("2026-10-02 12:22:00.77656516 +0000 UTC", "2026-10-02T12:22:00.7765651Z")]
    [InlineData("2026-10-02T10:15:10.738186962Z", "2026-10-02T10:15:10.7381869Z")]
    [InlineData("2026-10-02T13:39:13Z", "2026-10-02T13:39:13Z")]
    public void Both_timestamp_shapes_become_the_same_utc_instant_whatever_zone_the_cli_printed(string text, string utc)
    {
        var instant = DockerText.Instant(text);

        instant.Should().Be(Reading.Of(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture)));
        instant.ValueOr(default).Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Gos_zero_time_means_never_and_is_unavailable_not_year_one() =>
        DockerText.Instant("0001-01-01T00:00:00Z").Should().BeEquivalentTo(Reading.Missing<DateTimeOffset>("never (Docker's zero time)"));

    [Fact]
    public void A_percentage_is_read_without_its_sign()
    {
        DockerText.Percent("0.77%").Should().Be(Reading.Of(0.77));
        DockerText.Percent("--").IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void A_label_value_holding_a_comma_stays_whole()
    {
        var labels = DockerJson.Labels("com.docker.compose.project.config_files=a.yml,b.yml,org.testcontainers=true,desktop.docker.io/ports/5432/tcp=127.0.0.1:5466");

        labels.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["com.docker.compose.project.config_files"] = "a.yml,b.yml",
            ["org.testcontainers"] = "true",
            ["desktop.docker.io/ports/5432/tcp"] = "127.0.0.1:5466",
        });
    }

    [Fact]
    public void A_listing_with_a_line_that_is_not_json_is_unavailable_naming_the_line()
    {
        var parsed = DockerJson.Lines("{\"a\":\"1\"}\nnot json\n", "docker test", row => DockerText.Field(row, "a"));

        parsed.ReasonOrEmpty.Should().Be("line 2 of docker test is not a JSON object");
    }
}
