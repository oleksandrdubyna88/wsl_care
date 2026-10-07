using WslCare.Core.Config;
using WslCare.Core.Json;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WslCare.Core.Records;

/// <summary>What started a run (plan §6).</summary>
[JsonConverter(typeof(StrictStringEnumConverter<RunTrigger>))]
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
[JsonConverter(typeof(StrictStringEnumConverter<RunOutcome>))]
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

    /// <summary>A detached run that could not start (plan §15j B2): its <c>act --request</c> met the lock or a wedged run and
    /// wrote this TERMINAL line with the reason instead of a silent busy. Written by E6.S1; read since E6.S0 (<c>runs show</c>
    /// answers <c>refused</c>), so a history that carries one never makes a line unparseable.</summary>
    [JsonStringEnumMemberName("refused")]
    Refused,
}

/// <summary>What a run WAS (plan §15o): a full check (<c>collect</c>, whatever started it) or an <c>act</c>. It is the ONE rule a
/// reader tells a full check's history line by; <see cref="RunRecord.Actions"/> holds per-action results only and never names
/// the full check itself. Never serialized itself: on disk a kind is a <see cref="RecordedKind"/>, which also holds "absent" and
/// "unknown", and whose names are <see cref="RunKinds.Name"/> — the one table of them.</summary>
public enum RunKind
{
    Collect,

    Act,
}

/// <summary>What a run detail IS, read from its <c>kind</c> member (plan §15o, PR #16 retro round G0): a full run's detail
/// carries NO member, an act's says <c>act</c>, and anything else — an explicit <c>null</c>, <c>""</c>, <c>collect</c>, a future
/// kind, an integer — is a detail this build does not read.</summary>
public enum DetailKind
{
    FullRun,

    Act,

    Unknown,
}

/// <summary>The few places a run's kind is read from something that predates it, each one rule (plan §15o).</summary>
public static class RunKinds
{
    /// <summary>The full check's meta name — the request's <c>kind</c> and only action, <c>running.json</c>'s action and step
    /// while it measures. RESERVED: no action id may carry it (<c>ActionId</c> says so, a test holds it).</summary>
    public const string FullCheckName = "collect";

    /// <summary>An act's name, as a request's <c>kind</c> and an act's detail spell it.</summary>
    public const string ActName = "act";

    /// <summary>A kind's name on the wire — what the history line, <c>running.json</c> and <c>runs</c>' <c>kind</c> carry.</summary>
    public static string Name(RunKind kind) => kind == RunKind.Act ? ActName : FullCheckName;

    /// <summary>The kind <paramref name="name"/> spells EXACTLY; <c>null</c> for anything else (a different case included).</summary>
    public static RunKind? Known(string name) => name switch
    {
        FullCheckName => RunKind.Collect,
        ActName => RunKind.Act,
        _ => null,
    };

    /// <summary>A request's <c>kind</c>: <c>collect</c> or <c>act</c> exactly, anything else UNKNOWN (<c>null</c>) — never guessed
    /// into either (§15o review G2; the reader refuses such a request already, the mapping does not lean on it).</summary>
    public static RunKind? OfRequest(string kind) => Known(kind);

    /// <summary>A run detail's kind from its <c>kind</c> member: NO member is a full run's detail, <c>act</c> an act's, and any
    /// other value — present but not <c>act</c> — is <see cref="DetailKind.Unknown"/>, never a full run (§15o review G1, PR #16
    /// retro G0: an explicit <c>null</c> is a value, not a missing member). The one rule the reconcile and <c>logs</c> /
    /// <c>runs show</c> read a detail by.</summary>
    public static DetailKind OfDetail(RecordedKind member) =>
        member.IsAbsent ? DetailKind.FullRun
        : member == RecordedKind.Act ? DetailKind.Act
        : DetailKind.Unknown;

    /// <summary>The kind the line of a run with such a detail carries — none for a detail of an unknown kind.</summary>
    public static RecordedKind LineKindOf(DetailKind detail) => detail switch
    {
        DetailKind.FullRun => RecordedKind.Collect,
        DetailKind.Act => RecordedKind.Act,
        _ => RecordedKind.Absent,
    };
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
            && CanonicalPid(text[(dash + 1)..]);
        return wellFormed ? new RunId(text) : null;
    }

    /// <summary>A pid as <see cref="New"/> spells it — digits only, no leading zero — so one run has ONE id (E6.S0 review S4:
    /// <c>…-0123</c> and <c>…-123</c> would name the same process twice; a real pid never has a leading zero).</summary>
    private static bool CanonicalPid(string pid) =>
        int.TryParse(pid, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
        && pid == number.ToString(CultureInfo.InvariantCulture);

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
    public static int FailureLimit => Tuning.Current.Int(ConfigKeys.Records.MaxReasonChars);
}

/// <summary>One line of <c>history.jsonl</c> (plan §6). Both instants are UTC.</summary>
/// <param name="Actions">Per-action RESULTS (plan §6, §15o): the actions that ran, were skipped, refused, interrupted — never
/// the full check itself. <c>[]</c> means no action produced a result; whether the run was a full check is <paramref name="Kind"/>'s
/// answer.</param>
/// <param name="Kind">What the run was (plan §15o). POSITIONAL so every writer decides. Writers write <c>collect</c> or
/// <c>act</c>, or none (<see cref="RecordedKind.Absent"/>) where the kind is not known: an unusable request, an orphan whose
/// detail cannot be read or is of a kind this build does not know, the dead holder of a <c>running.json</c> that names no kind
/// and is not the exact full-check shape, or one whose kind this build does not know. Read back: absent on lines written before
/// it existed (additive, schema 1), and UNKNOWN — never guessed, never unparseable — for a value this build does not know (a
/// newer build's, met after a downgrade; PR #16 retro round).</param>
public sealed record RunRecord(
    int SchemaVersion,
    RunId RunId,
    RunTrigger Trigger,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    RunOutcome Outcome,
    IReadOnlyList<ActionRecord> Actions,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] RecordedKind Kind)
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
