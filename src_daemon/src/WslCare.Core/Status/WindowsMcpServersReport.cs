using WslCare.Core.Collectors;
using WslCare.Core.Mcp;

namespace WslCare.Core.Status;

// The `windowsMcpServers` block of the Windows binary's `status --json` (E14 S7a, read-only). Additive: the distro's binary omits
// it. As everywhere in this answer, an unread figure is `available: false` with its reason, never 0; nullable members only here,
// at the JSON edge.

/// <summary>The Windows side's MCP server processes.</summary>
/// <param name="HeldBytes">Σ private bytes over the instances whose memory was read.</param>
/// <param name="CpuCores">Σ CPU % ÷ 100 over the instances measured over the window.</param>
public sealed record WindowsMcpServersReport(
    bool Available,
    string? Reason,
    int? WindowMilliseconds,
    int? Count,
    int? Listed,
    int? IdleCount,
    int? OrphanedCount,
    long? HeldBytes,
    long? WorkingSetBytes,
    NumberFigure? CpuCores,
    IReadOnlyList<WindowsMcpServerReport>? Servers,
    IReadOnlyList<WindowsMcpOwnerGroup>? Owners,
    IReadOnlyList<WindowsMcpInstanceReport>? Instances)
{
    public static WindowsMcpServersReport From(Reading<WindowsMcpSample> reading) => reading switch
    {
        Reading<WindowsMcpSample>.Available { Value: var s } => new(
            true, null, s.WindowMilliseconds, s.Count, s.Listed.Count, s.IdleCount, s.OrphanedCount,
            s.Instances.Sum(i => i.PrivateBytes.ValueOr(0)), s.Instances.Sum(i => i.WorkingSet.ValueOr(0)), StatusReports.Number(Cores(s.Instances)),
            ServerReports(s.Instances), s.Owners, [.. s.Listed.Select(WindowsMcpInstanceReport.From)]),
        _ => new(false, reading.ReasonOrEmpty, null, null, null, null, null, null, null, null, null, null, null),
    };

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
            .Select(g => new WindowsMcpServerReport(g.Key, g.Count(), g.Count(i => i.Idle), g.Count(i => i.Owner.Kind == WindowsMcpOwner.Orphaned), g.Sum(i => i.WorkingSet.ValueOr(0)))),
    ];
}

/// <summary>One server on Windows: its instances, the idle and the orphaned among them, the working set they hold.</summary>
public sealed record WindowsMcpServerReport(string Name, int Count, int Idle, int Orphaned, long WorkingSetBytes);

/// <summary>One instance as the wire shows it.</summary>
public sealed record WindowsMcpInstanceReport(
    int Pid,
    string Server,
    WindowsMcpOwner Owner,
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
        i.Owner,
        i.Created is Reading<DateTimeOffset>.Available { Value: var at } ? at : null,
        StatusReports.Number(i.CpuPercent),
        StatusReports.Bytes(i.WorkingSet),
        StatusReports.Bytes(i.PrivateBytes),
        i.SessionId is Reading<int>.Available { Value: var session } ? session : null,
        i.Idle);
}
