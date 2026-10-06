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

    /// <summary>Every member one of the key's allowed values (§15q R1.3, review B1) — the refusal names the first that is not.</summary>
    private static ValueCheck CheckMembers(ConfigKey.TextListKey key, IReadOnlyList<string> members) =>
        members.FirstOrDefault(m => !key.Allowed.Contains(m, StringComparer.Ordinal)) is { } stranger
            ? new ValueCheck.Invalid($"{key.Name} must be {key.Kind}; got \"{stranger}\"")
            : new ValueCheck.Ok(new ConfigValue.TextList(members));

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
