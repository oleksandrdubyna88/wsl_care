using System.Text.Json.Serialization;

namespace WslCare.Core.Thresholds;

/// <summary>How a figure stands against its threshold. <c>unknown</c> is a figure that could not be read: it is
/// never taken for <c>ok</c> (plan §15b #7).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Level>))]
public enum Level
{
    [JsonStringEnumMemberName("ok")]
    Ok,

    [JsonStringEnumMemberName("warn")]
    Warn,

    [JsonStringEnumMemberName("critical")]
    Critical,

    [JsonStringEnumMemberName("unknown")]
    Unknown,
}

/// <summary>One threshold evaluated over a full run: which, the level, the figure as read, the limit in force and the
/// reason in a sentence.</summary>
/// <param name="Id">Stable, dotted: <c>memory.available</c>, <c>disk.root</c>, <c>docker.A4</c>, …</param>
/// <param name="Value">The figure as a person reads it (<c>21.3 %</c>); empty when unknown.</param>
/// <param name="Limit">The limit(s) applied (<c>warn &lt; 25 %, critical &lt; 15 %</c>).</param>
public sealed record Verdict(string Id, Level Level, string Value, string Limit, string Reason);
