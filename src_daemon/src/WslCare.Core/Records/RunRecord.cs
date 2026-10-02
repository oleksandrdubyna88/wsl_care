using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WslCare.Core.Records;

/// <summary>What started a run (plan §6).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RunTrigger>))]
public enum RunTrigger
{
    [JsonStringEnumMemberName("timer")]
    Timer,

    /// <summary>A button in the extension.</summary>
    [JsonStringEnumMemberName("manual")]
    Manual,

    /// <summary>Someone at a terminal.</summary>
    [JsonStringEnumMemberName("cli")]
    Cli,
}

/// <summary>How a run ended. <c>interrupted</c> is here from the first record (plan §15a #0): the
/// startup sweep that finds a dead <c>running.json</c> writes one, so history never shows a run that
/// silently vanished.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RunOutcome>))]
public enum RunOutcome
{
    [JsonStringEnumMemberName("completed")]
    Completed,

    [JsonStringEnumMemberName("failed")]
    Failed,

    [JsonStringEnumMemberName("interrupted")]
    Interrupted,

    /// <summary>The configuration had an invalid layer, so the run collected and reported but did not act (plan §15a #1).</summary>
    [JsonStringEnumMemberName("observeOnly")]
    ObserveOnly,
}

/// <summary>A run's identity: the UTC second it started and the process that ran it (plan §6).</summary>
[JsonConverter(typeof(RunIdJsonConverter))]
public sealed record RunId
{
    private const string StampFormat = "yyyyMMdd'T'HHmmss'Z'";

    private RunId(string text)
    {
        Text = text;
    }

    public string Text { get; }

    public static RunId New(DateTimeOffset startedAtUtc, int processId) =>
        new($"{startedAtUtc.UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture)}-{processId}");

    /// <summary>A stored id read back; <c>null</c> when the text is not one.</summary>
    public static RunId? TryParse(string text)
    {
        var dash = text.IndexOf('-');
        var wellFormed = dash > 0
            && DateTime.TryParseExact(text[..dash], StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _)
            && int.TryParse(text[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out _);
        return wellFormed ? new RunId(text) : null;
    }

    public override string ToString() => Text;
}

/// <summary>A <see cref="RunId"/> is one JSON string.</summary>
public sealed class RunIdJsonConverter : JsonConverter<RunId>
{
    public override RunId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        RunId.TryParse(reader.GetString() ?? string.Empty) ?? throw new JsonException("not a run id");

    public override void Write(Utf8JsonWriter writer, RunId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Text);
}

/// <summary>One action's line in a run (plan §6): what it was, how many objects, how many bytes measured freed.</summary>
public sealed record ActionRecord(string Id, int Count, long FreedBytes);

/// <summary>One line of <c>history.jsonl</c> (plan §6). Both instants are UTC.</summary>
public sealed record RunRecord(
    int SchemaVersion,
    RunId RunId,
    RunTrigger Trigger,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    RunOutcome Outcome,
    IReadOnlyList<ActionRecord> Actions);
