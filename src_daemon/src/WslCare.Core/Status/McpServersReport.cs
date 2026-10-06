using WslCare.Core.Collectors;
using WslCare.Core.Mcp;

namespace WslCare.Core.Status;

// The `mcpServers` block of `status --json` and of every full run's embedded sample (plan §15q E7.S2d, Decided 11). Additive:
// absent from every older daemon. As everywhere in this answer, a figure is `available` with its value or `available: false`
// with a reason — an unread figure is never 0 (plan §15b #7); nullable members only here, at the JSON edge.

/// <summary>The MCP servers of the AI agents.</summary>
/// <param name="Count">Every instance found; <paramref name="Listed"/> of them are in <paramref name="Instances"/>
/// (<c>mcpServers.maxInstances</c>).</param>
/// <param name="BusyWithoutActivityCount">Busy (CPU at or above the idle line) with no log write in the activity window.</param>
/// <param name="NotUnderAgent">Processes of a watched server under a live process that is no AI agent: not instances.</param>
/// <param name="CpuCores">Σ CPU across the window ÷ 100 over the <paramref name="CpuMeasured"/> instances measured.</param>
/// <param name="HeldBytes">Σ <c>RssAnon + RssShmem</c> over every instance.</param>
public sealed record McpServersReport(
    bool Available,
    string? Reason,
    int? WindowMilliseconds,
    int? Count,
    int? Listed,
    int? IdleCount,
    int? BusyWithoutActivityCount,
    int? NotUnderAgent,
    NumberFigure? CpuCores,
    int? CpuMeasured,
    long? HeldBytes,
    IReadOnlyList<McpServerReport>? Servers,
    IReadOnlyList<McpInstanceReport>? Instances)
{
    public static McpServersReport From(Reading<McpSample> reading) => reading switch
    {
        Reading<McpSample>.Available { Value: var s } => new(
            true, null, s.WindowMilliseconds, s.Count, s.Instances.Count, s.IdleCount, s.BusyWithoutActivityCount, s.NotUnderAgent,
            StatusReports.Number(s.CpuCores), s.CpuMeasured, s.HeldBytes, [.. s.Servers.Select(McpServerReport.From)], [.. s.Instances.Select(McpInstanceReport.From)]),
        _ => new(false, reading.ReasonOrEmpty, null, null, null, null, null, null, null, null, null, null, null),
    };
}

/// <summary>One watched server: its instances and its starts in the window, and how they were counted (<c>logNames</c> or the
/// lower bound <c>liveYounger</c>).</summary>
public sealed record McpServerReport(string Name, int Count, NumberFigure Starts, int StartsWindowMinutes, string StartsBasis)
{
    public static McpServerReport From(McpServerSummary s) =>
        new(s.Name, s.Count, StatusReports.Number(s.Starts.Count.Map(c => (double)c)), s.Starts.WindowMinutes, s.Starts.Basis);
}

/// <summary>Who owns an instance: the agent session above it, or none (<paramref name="Orphaned"/>).</summary>
public sealed record McpOwnerReport(bool Orphaned, int? Pid, string? Agent, string? CommandLine)
{
    public static McpOwnerReport From(McpOwner owner) => owner switch
    {
        McpOwner.Agent a => new(false, a.Pid, a.Name, a.CommandLine),
        _ => new(true, null, null, null),
    };
}

/// <summary>When an instance last wrote its run log, or why that cannot be told.</summary>
public sealed record McpActivityReport(bool Available, DateTimeOffset? LastLogWriteAt, string? Reason);

/// <param name="Kind"><c>unknown</c>, <c>starting</c>, <c>idle</c>, <c>busy</c> or <c>busyWithoutActivity</c>.</param>
public sealed record McpInstanceReport(
    int Pid,
    string Server,
    string User,
    string State,
    NumberFigure AgeSeconds,
    NumberFigure CpuPercent,
    long HeldBytes,
    string Kind,
    McpOwnerReport Owner,
    McpActivityReport Activity)
{
    public static McpInstanceReport From(McpInstance i) => new(
        i.Process.Pid,
        i.Server,
        i.Process.User,
        i.Process.State.ToString(),
        StatusReports.Number(i.Process.Age.Map(a => Math.Round(a.TotalSeconds))),
        StatusReports.Number(i.CpuPercent),
        i.Process.HeldBytes,
        KindName(i.Kind),
        McpOwnerReport.From(i.Owner),
        i.LastLogWrite is Reading<DateTimeOffset>.Available { Value: var at } ? new(true, at, null) : new(false, null, i.LastLogWrite.ReasonOrEmpty));

    public static string KindName(McpKind kind) => kind switch
    {
        McpKind.Starting => "starting",
        McpKind.Idle => "idle",
        McpKind.Busy => "busy",
        McpKind.BusyWithoutActivity => "busyWithoutActivity",
        _ => "unknown",
    };
}
