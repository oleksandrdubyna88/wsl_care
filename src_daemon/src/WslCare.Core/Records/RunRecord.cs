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
public sealed record ActionRecord(string Id, int Count, long FreedBytes)
{
    /// <summary>What became of it in this run (<c>ran</c>, <c>failed</c>, <c>dryRun</c>, <c>skipped</c>, <c>deferred</c>,
    /// <c>refused</c>, E3.S1); absent on lines written before the engine.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; init; }

    /// <summary>A dry run's preview bytes — what it WOULD have freed (plan §7.4: dry runs counted apart); absent otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? WouldFreeBytes { get; init; }

    /// <summary>Why a <c>failed</c> action failed, shortened to <see cref="FailureLimit"/> characters — so <c>logs</c> can show
    /// it beside the figures without opening the run's detail; absent otherwise and on lines written before it existed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Failure { get; init; }

    /// <summary>How much of a failure the history line keeps: one line of the log page, not a transcript.</summary>
    public const int FailureLimit = 300;
}

/// <summary>One line of <c>history.jsonl</c> (plan §6). Both instants are UTC.</summary>
public sealed record RunRecord(
    int SchemaVersion,
    RunId RunId,
    RunTrigger Trigger,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    RunOutcome Outcome,
    IReadOnlyList<ActionRecord> Actions)
{
    /// <summary>The slow parts a full run sampled (plan §15b #5); absent (<c>null</c>) on a run that sampled
    /// none, and on every line written before E2 — read only through <see cref="LastFullRun"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SlowParts? Slow { get; init; }

    /// <summary>The run's detail file, relative to the state directory with <c>/</c> separators
    /// (<c>runs/2026-10-02/20261002T120000Z-123.json</c>, plan §6); absent on a line that names none — before
    /// E2.S3, or a run whose detail could not be written (then <see cref="Reason"/> says why).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; init; }

    /// <summary>Whether the run was a dry run (plan §5: the timer's first week); absent before E2.S3.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DryRun { get; init; }

    /// <summary>Why the run ended <c>failed</c> or <c>interrupted</c>; absent when it completed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    /// <summary>The threshold verdicts that were not <c>ok</c> (plan §6: "warnings").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<WarningRecord>? Warnings { get; init; }

    /// <summary>The headline figures of the run (plan §6: "metrics") — what the Logs page's max / min reads.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunMetrics? Metrics { get; init; }

    /// <summary>The detail path, or empty — the one read of <see cref="Detail"/> (it is null on old lines).</summary>
    [JsonIgnore]
    public string DetailPath => Detail ?? string.Empty;
}

/// <summary>A threshold that was not ok, as the history line keeps it: its id, its level, and why.</summary>
public sealed record WarningRecord(string Id, string Level, string Reason);

/// <summary>The headline figures of one run, as the history line keeps them. A member is absent when the figure was
/// not read — never written as 0 (plan §15b #7).</summary>
public sealed record RunMetrics(
    double? MemAvailablePercent,
    long? MemAvailableBytes,
    long? PageCacheBytes,
    long? SwapUsedBytes,
    double? RootUsedPercent,
    long? DockerReclaimableBytes,
    int? ContainerStarts24h);
