using System.Text.Json;

using FluentAssertions;

using WslCare.Core;
using WslCare.Core.Json;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Records;

/// <summary>One run, one line of <c>history.jsonl</c> (plan §6), with <c>interrupted</c> among the outcomes from day one (plan §15a #0).</summary>
public sealed class RunRecordTests
{
    private static readonly DateTimeOffset Started = new(2026, 10, 2, 10, 15, 0, TimeSpan.Zero);

    private static RunRecord Record(RunOutcome outcome, int pid = 1234) =>
        new(SchemaVersion.Current, RunId.New(Started, pid), RunTrigger.Timer, Started, Started.AddSeconds(42), outcome, [new ActionRecord("A4", 3, 123_456_789)]);

    [Fact]
    public void A_run_id_is_the_utc_second_and_the_pid()
    {
        RunId.New(Started, 1234).Text.Should().Be("20261002T101500Z-1234");
        RunId.TryParse("20261002T101500Z-1234").Should().NotBeNull();
        RunId.TryParse("2026-10-02-1234").Should().BeNull();
        RunId.TryParse("20261002T101500Z-abc").Should().BeNull();
    }

    /// <summary>E6.S0 review S4: one run, one spelling — a pid with a leading zero (<c>…-0123</c>) would name the same
    /// process as <c>…-123</c> under a second id, and a real pid never has one.</summary>
    [Theory]
    [InlineData("20261002T101500Z-0123")]
    [InlineData("20261002T101500Z-00")]
    [InlineData("20261002T101500Z-+12")]
    public void A_run_id_is_accepted_only_in_its_canonical_spelling(string text) =>
        RunId.TryParse(text).Should().BeNull();

    [Fact]
    public void A_record_serializes_as_one_camel_case_line_with_string_enums_and_the_schema_version()
    {
        var json = JsonSerializer.Serialize(Record(RunOutcome.Interrupted), WslCareJsonContext.Compact.RunRecord);

        json.Should().NotContain("\n");
        json.Should().Contain("\"schemaVersion\":1")
            .And.Contain("\"runId\":\"20261002T101500Z-1234\"")
            .And.Contain("\"trigger\":\"timer\"")
            .And.Contain("\"outcome\":\"interrupted\"")
            .And.Contain("\"startedAt\":\"2026-10-02T10:15:00+00:00\"")
            .And.Contain("\"actions\":[{\"id\":\"A4\",\"count\":3,\"freedBytes\":123456789}]");
    }

    [Theory]
    [InlineData(RunOutcome.Completed)]
    [InlineData(RunOutcome.Failed)]
    [InlineData(RunOutcome.Interrupted)]
    [InlineData(RunOutcome.ObserveOnly)]
    public void Every_outcome_round_trips(RunOutcome outcome)
    {
        var json = JsonSerializer.Serialize(Record(outcome), WslCareJsonContext.Compact.RunRecord);

        var back = JsonSerializer.Deserialize(json, WslCareJsonContext.Compact.RunRecord);

        back.Should().NotBeNull();
        back!.Outcome.Should().Be(outcome);
        back.RunId.Text.Should().Be("20261002T101500Z-1234");
        back.Actions.Should().ContainSingle().Which.FreedBytes.Should().Be(123_456_789);
    }

    [Fact]
    public void The_writer_appends_one_line_per_record_in_the_state_directory()
    {
        using var host = new SandboxHost("records");
        var writer = new RunRecordWriter(host.Paths, host.Files);

        writer.Append(Record(RunOutcome.Completed, 1));
        writer.Append(Record(RunOutcome.ObserveOnly, 2));

        writer.HistoryFile.Should().Be(Path.Combine(host.Paths.StateDirectory, "history.jsonl"));
        var lines = File.ReadAllLines(writer.HistoryFile);
        lines.Should().HaveCount(2);
        lines[0].Should().Contain("\"outcome\":\"completed\"").And.Contain("-1\"");
        lines[1].Should().Contain("\"outcome\":\"observeOnly\"").And.Contain("-2\"");
        lines.Should().OnlyContain(l => JsonSerializer.Deserialize(l, WslCareJsonContext.Compact.RunRecord) != null);
    }
}
