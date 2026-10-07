using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WslCare.Core.Records;

/// <summary>
/// A record's <c>kind</c> member as it is ON DISK (plan §15o, the PR #16 retro round): one of THREE states, never collapsed —
/// <list type="bullet">
/// <item><b>absent</b> — the member is missing: a writer older than plan §15o, or a line whose kind could not be known. The
/// default value, so a record that does not carry the member reads as absent without a null;</item>
/// <item><b>known</b> — <c>collect</c> or <c>act</c>, exactly as <see cref="RunKinds.Name"/> spells them;</item>
/// <item><b>unknown</b> — anything else: a name a NEWER build wrote (met after a downgrade), an explicit <c>null</c>, an integer,
/// any other JSON value. Kept as it was written; never guessed into a known kind, and never makes its record unreadable.</item>
/// </list>
/// The legacy inferences (an older <c>running.json</c> of the exact full-check shape, a full run's detail with no member) apply
/// ONLY to absent: an unknown value is not a missing one. Writers write a known kind or none — an unknown kind read from disk is
/// carried forward as absent (<see cref="ForWriting"/>), and the converter refuses to write one.
/// </summary>
[JsonConverter(typeof(RecordedKindJsonConverter))]
public readonly record struct RecordedKind
{
    private enum State
    {
        Absent,
        Known,
        Unknown,
    }

    private readonly State _state;
    private readonly RunKind _known;
    private readonly string? _text;

    private RecordedKind(State state, RunKind known, string text)
    {
        _state = state;
        _known = known;
        _text = text;
    }

    /// <summary>No <c>kind</c> member.</summary>
    public static RecordedKind Absent => default;

    /// <summary>A full check.</summary>
    public static RecordedKind Collect { get; } = Of(RunKind.Collect);

    /// <summary>An <c>act</c>.</summary>
    public static RecordedKind Act { get; } = Of(RunKind.Act);

    public static RecordedKind Of(RunKind kind) => new(State.Known, kind, RunKinds.Name(kind));

    /// <summary>A value this build does not know, as it was written (a JSON string's text, else the JSON token itself).</summary>
    public static RecordedKind Unknown(string written) => new(State.Unknown, default, written);

    /// <summary>A kind's name: <c>collect</c> / <c>act</c> exactly — anything else, a different case included, is unknown.</summary>
    public static RecordedKind Parse(string name) => RunKinds.Known(name) is { } kind ? Of(kind) : Unknown(name);

    /// <summary>What a writer passes: a known kind, or <c>null</c> for a line that carries none (absent).</summary>
    public static implicit operator RecordedKind(RunKind? kind) => kind is { } known ? Of(known) : Absent;

    public bool IsAbsent => _state == State.Absent;

    public bool IsKnown => _state == State.Known;

    public bool IsUnknown => _state == State.Unknown;

    /// <summary>The known kind — refused for an absent or unknown one, whose backing value is the enum's default (<c>collect</c>)
    /// and must never be read as a kind (PR #44 gate round). Ask <see cref="IsKnown"/> first, or compare with
    /// <see cref="Collect"/> / <see cref="Act"/>.</summary>
    public RunKind Known => IsKnown ? _known : throw new InvalidOperationException($"the kind is {this}: it has no known kind");

    /// <summary>The name of a known kind, the value of an unknown one as written; empty when absent.</summary>
    public string Text => _text ?? string.Empty;

    /// <summary>What a writer may carry over from a record it READ: a known kind as it is, an unknown one as absent — an
    /// unknown value is never written back, so no line ever claims a kind this build only guessed at.</summary>
    public RecordedKind ForWriting => IsUnknown ? Absent : this;

    public override string ToString() => _state switch
    {
        State.Known => Text,
        State.Unknown => $"unknown \"{Text}\"",
        _ => "absent",
    };
}

/// <summary>
/// The <c>kind</c> member read without failing (plan §15o retro round, AOT: a converter the source generator instantiates, no
/// reflection): a string is a known name or an unknown value; ANY other token — <c>null</c>, a number (gate G2: <c>0</c> is
/// not the ordinal of <c>collect</c>), a boolean, an object, an array — is unknown. Writes only a known kind.
/// </summary>
public sealed class RecordedKindJsonConverter : JsonConverter<RecordedKind>
{
    /// <summary>An explicit <c>null</c> reaches <see cref="Read"/> — it is unknown, never bound like a missing member.</summary>
    public override bool HandleNull => true;

    public override RecordedKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? RecordedKind.Parse(reader.GetString() ?? string.Empty)
            : RecordedKind.Unknown(Written(ref reader));

    public override void Write(Utf8JsonWriter writer, RecordedKind value, JsonSerializerOptions options)
    {
        if (!value.IsKnown)
        {
            throw new InvalidOperationException($"a kind is written only when it is known ({value}): a writer carries an unknown kind forward as absent (RecordedKind.ForWriting)");
        }

        writer.WriteStringValue(value.Text);
    }

    /// <summary>The token as written; an object or an array is skipped whole and named by its shape.</summary>
    private static string Written(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            var shape = reader.TokenType == JsonTokenType.StartObject ? "{…}" : "[…]";
            reader.Skip();
            return shape;
        }

        return reader.HasValueSequence ? Encoding.UTF8.GetString(reader.ValueSequence) : Encoding.UTF8.GetString(reader.ValueSpan);
    }
}
