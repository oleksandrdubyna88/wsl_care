using System.Globalization;
using System.Text.Json;

namespace WslCare.Core.Config;

/// <summary>
/// One setting the daemon knows: its dotted name and what a valid value looks like. A closed set —
/// the five shapes below are every shape the schema has, so validation is a <c>switch</c> over this
/// type and nothing is reflected.
/// </summary>
/// <remarks>Plan §15q R1.3 (review B1): no shape takes free text. A text key carries a <see cref="TextRule"/> — a closed set
/// of values or a declared, validated shape — and a list key its allowed set, so no value can name something the closed
/// registry does not already hold (<c>processes.families</c> accepted <c>other</c>, the catch-all, until E7.S0). What a key
/// means to ROOT — which way is safe, whether only the machine layer may set it — is <see cref="Trust"/>.</remarks>
public abstract record ConfigKey(string Name)
{
    private ConfigKey(string name, string kind) : this(name)
    {
        Kind = kind;
    }

    /// <summary>What a valid value is, in the words of a refusal message.</summary>
    public string Kind { get; } = string.Empty;

    /// <summary>How a value of this key steers a root run, and which direction of change is the safe one (plan §15q R1.2,
    /// R1.6). <see cref="KeyTrust.Display"/> unless the register says otherwise.</summary>
    public KeyTrust Trust { get; init; } = KeyTrust.Display;

    public sealed record BoolKey(string Name) : ConfigKey(Name, "true or false");

    public sealed record IntKey(string Name, int Min, int Max)
        : ConfigKey(Name, $"a whole number from {Min.ToString(CultureInfo.InvariantCulture)} to {Max.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>A text value, accepted only by its <paramref name="Rule"/> — never free text.</summary>
    public sealed record TextKey(string Name, TextRule Rule) : ConfigKey(Name, Rule.Describe);

    /// <summary>A list whose every member passes <paramref name="Member"/>, at most <paramref name="MaxMembers"/> of them: CLOSED —
    /// every member one of <paramref name="Allowed"/> — or OPEN (<paramref name="Allowed"/> empty), every member a declared shape
    /// (plan E14 S2c: <c>mcpServers.programs</c>). Never free text either way.</summary>
    public sealed record TextListKey(string Name, IReadOnlyList<string> Allowed, TextRule Member, int MaxMembers)
        : ConfigKey(Name, ListKind(Allowed, Member, MaxMembers))
    {
        /// <summary>A CLOSED list: every member one of <paramref name="allowed"/>, any number of them.</summary>
        public TextListKey(string name, IReadOnlyList<string> allowed)
            : this(name, allowed, new TextRule.OneOf(allowed), int.MaxValue)
        {
        }

        private static string ListKind(IReadOnlyList<string> allowed, TextRule member, int maxMembers) =>
            allowed.Count > 0
                ? $"a list of: {string.Join(", ", allowed)} (comma-separated on the command line)"
                : $"a list of at most {maxMembers.ToString(CultureInfo.InvariantCulture)} members, each {member.Describe} (comma-separated on the command line)";
    }

    /// <summary>The manual AI agents (<c>aiAgents.extra</c>, plan §15q R2): a structured list whose SHAPE is
    /// <see cref="Agents.ExtraAgentShape"/>'s and whose folders are judged against the disk at every root read
    /// (<see cref="Agents.ExtraAgentRules"/>) — never free text.</summary>
    public sealed record AgentListKey(string Name) : ConfigKey(Name, Agents.ExtraAgentShape.Describe);
}

/// <summary>A setting's value — the same five shapes as <see cref="ConfigKey"/>.</summary>
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

    /// <summary>The manual AI agents, as <see cref="ConfigKey.AgentListKey"/> accepts them.</summary>
    public sealed record AgentList(IReadOnlyList<Agents.ExtraAgent> Agents) : ConfigValue
    {
        public override string Describe() => Agents.Count == 0 ? "(none)" : string.Join(", ", Agents.Select(a => a.Name));

        public override void WriteTo(Utf8JsonWriter writer) => WslCare.Core.Agents.ExtraAgentShape.Write(Agents, writer);

        public bool Equals(AgentList? other) => other is not null && Agents.SequenceEqual(other.Agents);

        public override int GetHashCode() => Agents.Count;
    }
}
