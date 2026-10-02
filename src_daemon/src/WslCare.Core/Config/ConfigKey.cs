using System.Globalization;
using System.Text.Json;

namespace WslCare.Core.Config;

/// <summary>
/// One setting the daemon knows: its dotted name and what a valid value looks like. A closed set —
/// the four shapes below are every shape the schema has, so validation is a <c>switch</c> over this
/// type and nothing is reflected.
/// </summary>
public abstract record ConfigKey(string Name)
{
    private ConfigKey(string name, string kind) : this(name)
    {
        Kind = kind;
    }

    /// <summary>What a valid value is, in the words of a refusal message.</summary>
    public string Kind { get; } = string.Empty;

    public sealed record BoolKey(string Name) : ConfigKey(Name, "true or false");

    public sealed record IntKey(string Name, int Min, int Max)
        : ConfigKey(Name, $"a whole number from {Min.ToString(CultureInfo.InvariantCulture)} to {Max.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>Free text when <paramref name="Allowed"/> is empty; otherwise exactly one of those values.</summary>
    public sealed record TextKey(string Name, IReadOnlyList<string> Allowed)
        : ConfigKey(Name, Allowed.Count == 0 ? "text" : $"one of: {string.Join(", ", Allowed)}");

    public sealed record TextListKey(string Name) : ConfigKey(Name, "a list of text values (comma-separated on the command line)");
}

/// <summary>A setting's value — the same four shapes as <see cref="ConfigKey"/>.</summary>
public abstract record ConfigValue
{
    private ConfigValue()
    {
    }

    /// <summary>The value as the terminal shows it.</summary>
    public abstract string Describe();

    public abstract void WriteTo(Utf8JsonWriter writer);

    /// <summary>The value as a JSON element — what the <c>--json</c> report carries.</summary>
    public JsonElement ToJsonElement()
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteTo(writer);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    public sealed record Bool(bool Value) : ConfigValue
    {
        public override string Describe() => Value ? "true" : "false";

        public override void WriteTo(Utf8JsonWriter writer) => writer.WriteBooleanValue(Value);
    }

    public sealed record Int(int Value) : ConfigValue
    {
        public override string Describe() => Value.ToString(CultureInfo.InvariantCulture);

        public override void WriteTo(Utf8JsonWriter writer) => writer.WriteNumberValue(Value);
    }

    public sealed record Text(string Value) : ConfigValue
    {
        public override string Describe() => Value;

        public override void WriteTo(Utf8JsonWriter writer) => writer.WriteStringValue(Value);
    }

    public sealed record TextList(IReadOnlyList<string> Values) : ConfigValue
    {
        public override string Describe() => string.Join(", ", Values);

        public override void WriteTo(Utf8JsonWriter writer)
        {
            writer.WriteStartArray();
            foreach (var value in Values)
            {
                writer.WriteStringValue(value);
            }

            writer.WriteEndArray();
        }

        public bool Equals(TextList? other) => other is not null && Values.SequenceEqual(other.Values, StringComparer.Ordinal);

        public override int GetHashCode() => Values.Count;
    }
}
