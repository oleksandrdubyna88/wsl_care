using System.Globalization;
using System.Text.Json;

namespace WslCare.Core.Config;

/// <summary>A value that passed, or the sentence saying why it did not.</summary>
public abstract record ValueCheck
{
    private ValueCheck()
    {
    }

    public sealed record Ok(ConfigValue Value) : ValueCheck;

    public sealed record Invalid(string Message) : ValueCheck;
}

/// <summary>A list member a LAYER holds that this build refuses only by what its catalogues hold, and why — left out by the
/// loader with a notice (plan E14 S2c, own code review finding 3).</summary>
public sealed record LeftOutMember(string Member, string Why);

/// <summary>A layer's value as the loader takes it: the check of what remains, and the members left out.</summary>
public sealed record LayerCheck(ValueCheck Check, IReadOnlyList<LeftOutMember> LeftOut);

/// <summary>
/// Whether a value fits a key — from a configuration file (<see cref="Check(ConfigKey, JsonElement)"/>)
/// or from the command line (<see cref="Parse"/>). The same ranges, the same sentences, one place.
/// </summary>
public static class ConfigValidation
{
    public static ValueCheck Check(ConfigKey key, JsonElement value) => key switch
    {
        ConfigKey.BoolKey => CheckBool(key, value),
        ConfigKey.IntKey range => CheckInt(range, value),
        ConfigKey.TextKey text => CheckText(text, value),
        ConfigKey.TextListKey list => CheckList(list, value),
        ConfigKey.AgentListKey agents => CheckAgents(agents, value),
        _ => new ValueCheck.Invalid($"{key.Name}: unsupported key shape"),
    };

    /// <summary>A LAYER's value, as the loader takes it: <see cref="Check(ConfigKey, JsonElement)"/>, except that a list member
    /// refused only by this build's catalogues (<see cref="TextRule.Outdated"/>) is left out instead of failing the layer — an
    /// upgrade that refuses more names must not make every run observe-only (plan E14 S2c, own code review finding 3). A
    /// malformed member is still an error; <c>config set</c> (<see cref="Parse"/>) still refuses every one.</summary>
    public static LayerCheck CheckLayer(ConfigKey key, JsonElement value) =>
        key is ConfigKey.TextListKey list && IsTextArray(value)
            ? CheckLayerList(list, [.. value.EnumerateArray().Select(e => e.GetString() ?? string.Empty)])
            : new LayerCheck(Check(key, value), []);

    private static LayerCheck CheckLayerList(ConfigKey.TextListKey key, IReadOnlyList<string> members)
    {
        var leftOut = members.Select(m => new LeftOutMember(m, key.Member.Outdated(m))).Where(l => l.Why.Length > 0).ToList();
        return new LayerCheck(CheckMembers(key, [.. members.Where(m => leftOut.All(l => !string.Equals(l.Member, m, StringComparison.Ordinal)))]), leftOut);
    }

    private static bool IsTextArray(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String);

    /// <summary>The command-line spelling of a value for <paramref name="key"/>.</summary>
    public static ValueCheck Parse(ConfigKey key, string text) => key switch
    {
        ConfigKey.BoolKey => ParseBool(key, text),
        ConfigKey.IntKey range => ParseInt(range, text),
        ConfigKey.TextKey allowed => CheckAllowed(allowed, text),
        ConfigKey.TextListKey list => CheckMembers(list, SplitList(text)),
        ConfigKey.AgentListKey agents => ParseAgents(agents, text),
        _ => new ValueCheck.Invalid($"{key.Name}: unsupported key shape"),
    };

    /// <summary>Plan §15q R2.1: the shape of every entry, the first problem named.</summary>
    private static ValueCheck CheckAgents(ConfigKey.AgentListKey key, JsonElement value) =>
        Agents.ExtraAgentShape.Read(value) switch
        {
            { Problem.Length: > 0 } invalid => new ValueCheck.Invalid($"{key.Name}: {invalid.Problem}"),
            var list => new ValueCheck.Ok(new ConfigValue.AgentList(list.Agents)),
        };

    /// <summary>The JSON text <c>config set aiAgents.extra -</c> read from stdin.</summary>
    private static ValueCheck ParseAgents(ConfigKey.AgentListKey key, string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return CheckAgents(key, document.RootElement);
        }
        catch (JsonException)
        {
            return new ValueCheck.Invalid($"{key.Name} must be {key.Kind}; what was given is not JSON");
        }
    }

    private static ValueCheck CheckBool(ConfigKey key, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => new ValueCheck.Ok(new ConfigValue.Bool(true)),
        JsonValueKind.False => new ValueCheck.Ok(new ConfigValue.Bool(false)),
        _ => Expected(key, value),
    };

    private static ValueCheck CheckInt(ConfigKey.IntKey key, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            return Expected(key, value);
        }

        return InRange(key, number);
    }

    private static ValueCheck CheckText(ConfigKey.TextKey key, JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? CheckAllowed(key, value.GetString() ?? string.Empty) : Expected(key, value);

    private static ValueCheck CheckList(ConfigKey.TextListKey key, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
        {
            return Expected(key, value);
        }

        return CheckMembers(key, [.. value.EnumerateArray().Select(e => e.GetString() ?? string.Empty)]);
    }

    /// <summary>Every member passes the key's member rule (§15q R1.3, review B1: one of the allowed values for a closed list), and
    /// no more members than the key's cap (plan E14 S2c) — the refusal names the first member that fails, and why for an open list.</summary>
    private static ValueCheck CheckMembers(ConfigKey.TextListKey key, IReadOnlyList<string> members) =>
        (members.Count > key.MaxMembers, members.Select(m => (Member: m, Problem: MemberProblem(key, m))).FirstOrDefault(p => p.Problem.Length > 0)) switch
        {
            (true, _) => new ValueCheck.Invalid($"{key.Name} must be {key.Kind}; got {members.Count.ToString(CultureInfo.InvariantCulture)} members"),
            (_, { Member: { } stranger, Problem: var why }) when key.Allowed.Count == 0 => new ValueCheck.Invalid($"{key.Name} must be {key.Kind}; \"{stranger}\" is refused: {why}"),
            (_, { Member: { } stranger }) => new ValueCheck.Invalid($"{key.Name} must be {key.Kind}; got \"{stranger}\""),
            _ => new ValueCheck.Ok(new ConfigValue.TextList(members)),
        };

    /// <summary>A closed list's member must be one of its allowed values AND pass its member rule — a rule narrows a catalogue,
    /// it never replaces it (coai code round 2026-10-08, finding 1).</summary>
    private static string MemberProblem(ConfigKey.TextListKey key, string member) =>
        key.Allowed.Count > 0 && !key.Allowed.Contains(member, StringComparer.Ordinal) ? "not one of the allowed values" : key.Member.Problem(member);

    private static ValueCheck ParseBool(ConfigKey key, string text) => text.ToLowerInvariant() switch
    {
        "true" => new ValueCheck.Ok(new ConfigValue.Bool(true)),
        "false" => new ValueCheck.Ok(new ConfigValue.Bool(false)),
        _ => new ValueCheck.Invalid($"{key.Name} must be {key.Kind}; got \"{text}\""),
    };

    private static ValueCheck ParseInt(ConfigKey.IntKey key, string text) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            ? InRange(key, number)
            : new ValueCheck.Invalid($"{key.Name} must be {key.Kind}; got \"{text}\"");

    private static ValueCheck InRange(ConfigKey.IntKey key, int number) =>
        number >= key.Min && number <= key.Max
            ? new ValueCheck.Ok(new ConfigValue.Int(number))
            : new ValueCheck.Invalid($"{key.Name} must be {key.Kind}; got {number.ToString(CultureInfo.InvariantCulture)}");

    private static ValueCheck CheckAllowed(ConfigKey.TextKey key, string text) =>
        key.Rule.Problem(text).Length == 0
            ? new ValueCheck.Ok(new ConfigValue.Text(text))
            : new ValueCheck.Invalid($"{key.Name} must be {key.Kind}; got \"{text}\"");

    private static IReadOnlyList<string> SplitList(string text) =>
        [.. text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

    private static ValueCheck Expected(ConfigKey key, JsonElement value) =>
        new ValueCheck.Invalid($"{key.Name} must be {key.Kind}; got {Describe(value)}");

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => $"the text \"{value.GetString()}\"",
        JsonValueKind.Number => $"the number {value.GetRawText()}",
        JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        JsonValueKind.Array => "a list",
        JsonValueKind.Object => "an object",
        _ => "null",
    };
}
