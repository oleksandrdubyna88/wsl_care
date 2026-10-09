using WslCare.Core.Collectors;
using WslCare.Core.Mcp;

namespace WslCare.Core.Status;

// The `windowsMcpServers` block of the Windows binary's `status --json` (E14 S7a, read-only). Additive: the distro's binary omits
// it. As everywhere in this answer, an unread figure is `available: false` with its reason, never 0; nullable members only here,
// at the JSON edge.

/// <summary>The Windows side's MCP server processes.</summary>
/// <param name="MemoryRead">How many instances' memory was read: <paramref name="Held"/> and <paramref name="WorkingSet"/> sum
/// those (coai code round 2026-10-09, findings 2, 3, 6 — a partial sum says what it covers).</param>
/// <param name="Held">Σ private bytes over the instances whose memory was read; unavailable when instances exist and none was.</param>
/// <param name="CpuCores">Σ CPU % ÷ 100 over the instances measured over the window.</param>
public sealed record WindowsMcpServersReport(
    bool Available,
    string? Reason,
    int? WindowMilliseconds,
    int? Count,
    int? Listed,
    int? IdleCount,
    int? OrphanedCount,
    int? MemoryRead,
    ByteFigure? Held,
    ByteFigure? WorkingSet,
    NumberFigure? CpuCores,
    IReadOnlyList<WindowsMcpServerReport>? Servers,
    IReadOnlyList<WindowsMcpOwnerGroupReport>? Owners,
    IReadOnlyList<WindowsMcpInstanceReport>? Instances)
{
    public static WindowsMcpServersReport From(Reading<WindowsMcpSample> reading) => reading switch
    {
        Reading<WindowsMcpSample>.Available { Value: var s } => new(
            true, null, s.WindowMilliseconds, s.Count, s.Listed.Count, s.IdleCount, s.OrphanedCount, s.Instances.Count(i => i.PrivateBytes.IsAvailable),
            StatusReports.Bytes(Sum(s.Instances, i => i.PrivateBytes)), StatusReports.Bytes(Sum(s.Instances, i => i.WorkingSet)), StatusReports.Number(Cores(s.Instances)),
            ServerReports(s.Instances), [.. s.Owners.Select(o => new WindowsMcpOwnerGroupReport(WindowsMcpOwnerReport.KindName(o.Kind), o.Parent, o.Count))], [.. s.Listed.Select(WindowsMcpInstanceReport.From)]),
        _ => new(false, reading.ReasonOrEmpty, null, null, null, null, null, null, null, null, null, null, null, null),
    };

    /// <summary>Σ over the instances whose figure was read; unavailable, with the first reason, when instances exist and none was
    /// read — an unread figure is never summed as a 0 that looks measured.</summary>
    private static Reading<long> Sum(IReadOnlyList<WindowsMcpInstance> instances, Func<WindowsMcpInstance, Reading<long>> figure) =>
        instances.Count > 0 && !instances.Any(i => figure(i).IsAvailable)
            ? Reading.Missing<long>($"no instance's memory could be read: {figure(instances[0]).ReasonOrEmpty}")
            : Reading.Of(instances.Sum(i => figure(i).ValueOr(0)));

    /// <summary>Σ CPU % ÷ 100 over the measured instances; unavailable when instances exist and none could be measured.</summary>
    private static Reading<double> Cores(IReadOnlyList<WindowsMcpInstance> instances) =>
        instances.Count > 0 && !instances.Any(i => i.CpuPercent.IsAvailable)
            ? Reading.Missing<double>("no instance's CPU could be measured across the window")
            : Reading.Of(Math.Round(instances.Sum(i => i.CpuPercent.ValueOr(0)) / McpSample.PercentPerCore, 2));

    private static List<WindowsMcpServerReport> ServerReports(IReadOnlyList<WindowsMcpInstance> instances) =>
    [
        .. instances
            .GroupBy(i => i.Server, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new WindowsMcpServerReport(g.Key, g.Count(), g.Count(i => i.Idle), g.Count(i => i.Owner.Kind == WindowsMcpOwnerKind.Orphaned), StatusReports.Bytes(Sum([.. g], i => i.WorkingSet)))),
    ];
}

/// <summary>One server on Windows: its instances, the idle and the orphaned among them, the working set they hold.</summary>
public sealed record WindowsMcpServerReport(string Name, int Count, int Idle, int Orphaned, ByteFigure WorkingSet);

/// <summary>One instance as the wire shows it.</summary>
public sealed record WindowsMcpInstanceReport(
    int Pid,
    string Server,
    WindowsMcpOwnerReport Owner,
    DateTimeOffset? Created,
    NumberFigure CpuPercent,
    ByteFigure WorkingSet,
    ByteFigure PrivateBytes,
    int? SessionId,
    bool Idle)
{
    public static WindowsMcpInstanceReport From(WindowsMcpInstance i) => new(
        i.Pid,
        i.Server,
        WindowsMcpOwnerReport.From(i.Owner),
        i.Created is Reading<DateTimeOffset>.Available { Value: var at } ? at : null,
        StatusReports.Number(i.CpuPercent),
        StatusReports.Bytes(i.WorkingSet),
        StatusReports.Bytes(i.PrivateBytes),
        i.SessionId is Reading<int>.Available { Value: var session } ? session : null,
        i.Idle);
}

/// <summary>Who holds an instance: <c>agent</c>, <c>interop</c>, <c>orphaned</c> or <c>other</c>, the direct parent, and why.</summary>
public sealed record WindowsMcpOwnerReport(string Kind, int ParentPid, string ParentName, string Detail)
{
    public static WindowsMcpOwnerReport From(WindowsMcpOwner owner) => new(KindName(owner.Kind), owner.ParentPid, owner.ParentName, owner.Detail);

    /// <summary>The kind as the wire names it.</summary>
    public static string KindName(WindowsMcpOwnerKind kind) => kind switch
    {
        WindowsMcpOwnerKind.Agent => "agent",
        WindowsMcpOwnerKind.Interop => "interop",
        WindowsMcpOwnerKind.Orphaned => "orphaned",
        _ => "other",
    };
}

/// <summary>How many instances one owner holds — the "35 under one wsl.exe" line.</summary>
public sealed record WindowsMcpOwnerGroupReport(string Kind, string Parent, int Count);
