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
public sealed record Verdict(string Id, Level Level, string Value, string Limit, string Reason)
{
    /// <summary>Where a verdict <c>status</c> shows came from (plan §15g B1): this sample, or the newest full run with its
    /// age. Absent (<c>null</c>) on the verdicts a full run writes into its own detail — there the run IS the basis — and
    /// on every detail written before E5.S0.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VerdictBasis? Basis { get; init; }
}

/// <summary>Which evaluation a verdict in <c>status --json</c> is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VerdictSource>))]
public enum VerdictSource
{
    /// <summary>Evaluated NOW, over this status sample and the effective configuration, by <see cref="ThresholdRules"/>.</summary>
    [JsonStringEnumMemberName("sample")]
    Sample,

    /// <summary>A threshold only a full run can judge (it needs a slow part: a tool, a log, Docker), carried exactly as
    /// the newest full run recorded it — or <c>unknown</c> with the reason there is none.</summary>
    [JsonStringEnumMemberName("fullRun")]
    FullRun,
}

/// <summary>The basis of a verdict <c>status</c> shows.</summary>
/// <param name="RunId">The full run the verdict was carried from; absent for a <c>sample</c> verdict and when no full
/// run's verdict could be read.</param>
/// <param name="EvaluatedAt">When it was evaluated: this sample's instant, or the end of that full run; absent when
/// there is no full run to name.</param>
/// <param name="AgeSeconds">How old the evaluation is at the time of the answer (0 for a <c>sample</c> verdict).</param>
public sealed record VerdictBasis(VerdictSource Source, string? RunId, DateTimeOffset? EvaluatedAt, double? AgeSeconds);
