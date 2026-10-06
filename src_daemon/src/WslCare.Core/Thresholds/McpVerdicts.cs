using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Mcp;

namespace WslCare.Core.Thresholds;

/// <summary>
/// The MCP servers' three thresholds (plan §15q E7.S2d, Decided 12) over one <see cref="McpSample"/> — evaluated by <c>status</c>
/// NOW and by every full run into its detail, each limit a key: <c>mcp.instances</c>, <c>mcp.cpu</c>, <c>mcp.starts</c>. Pure.
/// </summary>
public static class McpVerdicts
{
    public const string Instances = "mcp.instances";
    public const string Cpu = "mcp.cpu";
    public const string Starts = "mcp.starts";

    public static IReadOnlyList<Verdict> From(Reading<McpSample> sample, EffectiveConfig config) =>
    [
        InstancesVerdict(sample, config.Int(ConfigKeys.McpServers.WarnInstances)),
        CpuVerdict(sample, config.Int(ConfigKeys.McpServers.WarnCpuPercent)),
        StartsVerdict(sample, config.Int(ConfigKeys.McpServers.WarnStarts)),
    ];

    private static Verdict InstancesVerdict(Reading<McpSample> sample, int warnAbove)
    {
        var limit = Invariant($"warn > {warnAbove} instances (mcpServers.warnInstances)");
        return sample switch
        {
            Reading<McpSample>.Available { Value: var s } => new(
                Instances,
                s.Count > warnAbove ? Level.Warn : Level.Ok,
                Invariant($"{s.Count} instance(s): {s.IdleCount} idle, {s.BusyWithoutActivityCount} busy with no log write in the activity window{OverListed(s)}"),
                limit,
                "MCP server processes of the AI agents, one per agent session"),
            var unknown => new(Instances, Level.Unknown, string.Empty, limit, unknown.ReasonOrEmpty),
        };
    }

    private static Verdict CpuVerdict(Reading<McpSample> sample, int warnAbovePercent)
    {
        var limit = Invariant($"warn > {warnAbovePercent} % of one core in total (mcpServers.warnCpuPercent)");
        return sample.Bind(s => s.CpuCores.Map(cores => (Sample: s, Cores: cores))) switch
        {
            Reading<(McpSample Sample, double Cores)>.Available { Value: var v } => new(
                Cpu,
                v.Cores * McpSample.PercentPerCore > warnAbovePercent ? Level.Warn : Level.Ok,
                Invariant($"{v.Cores:0.00} core(s) over {v.Sample.CpuMeasured} measured instance(s)"),
                limit,
                Invariant($"CPU the agents' MCP servers burn together; {v.Sample.BusyWithoutActivityCount} of them busy with no log write in the activity window")),
            var unknown => new(Cpu, Level.Unknown, string.Empty, limit, unknown.ReasonOrEmpty),
        };
    }

    private static Verdict StartsVerdict(Reading<McpSample> sample, int warnAbove)
    {
        var limit = Invariant($"warn when a server started more than {warnAbove} times in the starts window (mcpServers.warnStarts)");
        return sample switch
        {
            Reading<McpSample>.Available { Value: var s } => StartsOf(s.Servers, warnAbove, limit),
            var unknown => new(Starts, Level.Unknown, string.Empty, limit, unknown.ReasonOrEmpty),
        };
    }

    /// <summary>Warn when any server's counted starts pass the limit; else unknown when any server's could not be counted; else ok.</summary>
    private static Verdict StartsOf(IReadOnlyList<McpServerSummary> servers, int warnAbove, string limit)
    {
        var counted = servers.Where(s => s.Starts.Count.IsAvailable).ToList();
        var storm = counted.Where(s => s.Starts.Count.ValueOr(0) > warnAbove).ToList();
        var value = string.Join("; ", counted.Select(s => Invariant($"{s.Name} {s.Starts.Count.ValueOr(0)} in {s.Starts.WindowMinutes} min{(s.Starts.Basis == McpStartsBasis.LiveYounger ? " (live instances only, a lower bound)" : string.Empty)}")));
        return storm.Count > 0 ? new(Starts, Level.Warn, value, limit, Invariant($"{string.Join(", ", storm.Select(s => s.Name))} started more often than the limit — a restart storm: the agent's MCP connect timeout kills a slow start and starts it again"))
            : counted.Count < servers.Count ? new(Starts, Level.Unknown, value, limit, string.Join("; ", servers.Where(s => !s.Starts.Count.IsAvailable).Select(s => $"{s.Name}: {s.Starts.Count.ReasonOrEmpty}")))
            : new(Starts, Level.Ok, value, limit, "how often each watched MCP server was started lately");
    }

    /// <summary>coai code round finding 3: past <c>mcpServers.maxInstances</c> the idle, busy and CPU figures cover the listed
    /// instances only — said, never left to read as all of them.</summary>
    public static string OverListed(McpSample sample) =>
        sample.Instances.Count < sample.Count ? Invariant($" (CPU, idle and busy figures over the {sample.Instances.Count} listed: mcpServers.maxInstances)") : string.Empty;

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
