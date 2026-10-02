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
        ConfigKey.TextListKey => CheckList(key, value),
        _ => new ValueCheck.Invalid($"{key.Name}: unsupported key shape"),
    };

    /// <summary>The command-line spelling of a value for <paramref name="key"/>.</summary>
    public static ValueCheck Parse(ConfigKey key, string text) => key switch
    {
        ConfigKey.BoolKey => ParseBool(key, text),
        ConfigKey.IntKey range => ParseInt(range, text),
        ConfigKey.TextKey allowed => CheckAllowed(allowed, text),
        ConfigKey.TextListKey => new ValueCheck.Ok(new ConfigValue.TextList(SplitList(text))),
        _ => new ValueCheck.Invalid($"{key.Name}: unsupported key shape"),
    };

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

    private static ValueCheck CheckList(ConfigKey key, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
        {
            return Expected(key, value);
        }

        return new ValueCheck.Ok(new ConfigValue.TextList([.. value.EnumerateArray().Select(e => e.GetString() ?? string.Empty)]));
    }

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
        key.Allowed.Count == 0 || key.Allowed.Contains(text, StringComparer.Ordinal)
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
