using System.Text.Json.Serialization;

namespace WslCare.Core.Agents;

/// <summary>One data folder of one agent as a walk measured it (plan §15q R2.3): sizes and counts, never contents.</summary>
/// <param name="Exists">The folder was there.</param>
/// <param name="Bytes">The summed length of its files, links and never-entered folders excluded.</param>
/// <param name="Files">How many files were counted.</param>
/// <param name="Complete">The walk reached its end; <c>false</c> when a ceiling stopped it or it was not measured at all.</param>
/// <param name="Excluded">What the walk declined to enter, each named once (<c>memory (never entered)</c>, a prefix,
/// <c>&lt;folder&gt; (different filesystem)</c>).</param>
/// <param name="Reason">Why the figures are not whole: a ceiling, unreadable, not measured this run; empty when whole.</param>
public sealed record AgentFolderSize(string Path, bool Exists, long Bytes, long Files, bool Complete, IReadOnlyList<string> Excluded, string Reason);

/// <summary>One session as a listing names it — its path relative to the folder the layout starts in, and its length.</summary>
public sealed record SessionName(string Name, long Bytes);

/// <summary>The sessions of one agent (plan §15q D2): counted only over a confirmed layout, by listing names and stat-ing
/// entries — never opening a file. <see cref="Counted"/> false (with <see cref="Reason"/>) is "—", never 0.</summary>
public sealed record SessionFigures(bool Counted, int Count, DateTimeOffset? Oldest, DateTimeOffset? Newest, long LargestBytes, bool Complete, string Reason)
{
    public static SessionFigures NotCounted(string reason) => new(false, 0, null, null, 0, false, reason);
}

/// <summary>One agent as a walk measured it. Persisted on the history line (<c>slow.agents</c>) with TOTALS, counts and dates
/// only — <see cref="Largest"/>, the five largest sessions by NAME, is answered live by <c>agents list --measure</c> and never
/// written to <c>history.jsonl</c> (plan §15q D1, review minor: less growth, less exposure).</summary>
public sealed record AgentSize(string Id, IReadOnlyList<AgentFolderSize> Folders, SessionFigures Sessions)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SessionName>? Largest { get; init; }

    /// <summary>The bytes of every folder that was measured.</summary>
    [JsonIgnore]
    public long TotalBytes => (Folders ?? []).Sum(f => f?.Bytes ?? 0);

    /// <summary>Every folder was there and measured whole.</summary>
    [JsonIgnore]
    public bool Whole => (Folders ?? []).All(f => f is not null && (!f.Exists || f.Complete));
}

/// <summary>The AI-agent folders of plan §4.6 as one walk measured them — the daily walk's <c>slow.agents</c>, or an
/// <c>agents list --measure</c>.</summary>
public sealed record AgentsSample(DateTimeOffset SampledAt, IReadOnlyList<AgentSize> Agents)
{
    /// <summary>The agent of that id, or <c>null</c> — a legitimate "not measured".</summary>
    public AgentSize? Find(string id) => (Agents ?? []).FirstOrDefault(a => a?.Id == id);
}
