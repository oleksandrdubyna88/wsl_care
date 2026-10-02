using System.Text.Json;
using System.Text.Json.Serialization;

namespace WslCare.Core.Config;

/// <summary>The three layers of plan §6, lowest precedence first.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConfigLayer>))]
public enum ConfigLayer
{
    /// <summary>The embedded <c>default.json</c>.</summary>
    [JsonStringEnumMemberName("default")]
    Default,

    /// <summary><c>/etc/wsl-care/config.json</c> or <c>%ProgramData%\wsl-care\config.json</c>.</summary>
    [JsonStringEnumMemberName("machine")]
    Machine,

    /// <summary>The user's overrides — the file <c>config set</c> writes.</summary>
    [JsonStringEnumMemberName("user")]
    User,
}

/// <summary>A layer and the file it is read from.</summary>
public sealed record ConfigLayerFile(ConfigLayer Layer, string Path);

/// <summary>
/// What is wrong with one configuration layer (plan §15a #1): the file, the line, and a sentence.
/// Line 0 means the whole file (unreadable, or not a JSON object).
/// </summary>
public sealed record ConfigError(ConfigLayerFile File, int Line, string Message)
{
    /// <summary>The one-line form every surface prints: <c>{file}:{line}: {message}</c>.</summary>
    public string Display => Line > 0 ? $"{File.Path}:{Line}: {Message}" : $"{File.Path}: {Message}";
}

/// <summary>One setting as the effective configuration holds it: the value and the layer it came from.</summary>
public sealed record ConfigEntry(ConfigKey Key, ConfigValue Value, ConfigLayer Layer);

/// <summary>The <c>configError</c> element of plan §15a #1, as every JSON answer carries it.</summary>
public sealed record ConfigErrorReport(string File, int Line, string Message)
{
    public static ConfigErrorReport From(ConfigError error) => new(error.File.Path, error.Line, error.Message);
}

/// <summary>One value in <c>config get --json</c>: the key, its value, and which layer set it.</summary>
public sealed record ConfigValueReport(string Key, JsonElement Value, ConfigLayer Layer)
{
    public static ConfigValueReport From(ConfigEntry entry) => new(entry.Key.Name, entry.Value.ToJsonElement(), entry.Layer);
}

/// <summary>The answer of <c>config get --json</c> (plan §6: every JSON answer carries <c>schemaVersion</c>).</summary>
public sealed record ConfigReport(
    int SchemaVersion,
    bool ObserveOnly,
    IReadOnlyList<ConfigErrorReport> ConfigError,
    IReadOnlyList<ConfigValueReport> Values);
