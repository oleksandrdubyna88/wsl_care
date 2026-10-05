using System.Text;
using System.Text.Json;

using WslCare.Core.Agents;
using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Json;
using WslCare.Core.Records;

namespace WslCare.Cli.Commands;

/// <summary>
/// <c>agents list [--measure] [--json]</c> (plan §4.6, §6, §15q E7.S1): which catalogue agents are here, how they were
/// detected, their version read from disk (never by running them, D3), and their folders' sizes and sessions — from the newest
/// full run that walked them (with its age), or measured now with <c>--measure</c> (as this process, recording nothing, under
/// <see cref="AgentWalk.MeasureNowBudget"/>). Read-only: nothing is opened inside an agent's folder, nothing written.
/// </summary>
internal static class AgentsCommand
{
    public static int Run(Request.AgentsList request, CliHost host, ConfigLoadResult loaded, TextWriter stdout, CancellationToken cancellationToken)
    {
        var now = host.Clock.GetUtcNow();
        var found = AgentDiscovery.Discover(host.Paths, host.Files, Environment.GetEnvironmentVariable("PATH"), host.Privilege.IsRoot);
        var last = LastFullRun.Read(host.Paths, host.Files, host.Clock);
        var (sizes, previous) = request.Measure
            ? Measured(host, found, last, cancellationToken)
            : Recorded(last);
        var report = AgentsReports.From(host.Paths.Side == Core.Hosting.HostSide.Wsl ? "wsl" : "windows", now, found, sizes, previous, loaded.Config);
        return Output.Answer(stdout, request.Json ? JsonSerializer.Serialize(report, WslCareJsonContext.Default.AgentsReport) : Render(report));
    }

    /// <summary>Measured now: every tracked agent, the five largest sessions by name kept (a live answer); its growth against the
    /// newest recorded walk.</summary>
    private static (AgentSizes, AgentsSample?) Measured(CliHost host, IReadOnlyList<AgentPresence> found, LastSlowParts last, CancellationToken cancellationToken)
    {
        var targets = found.Where(p => p.Tracked).Select(p => p.Target).ToList();
        var sample = new AgentWalk(host.Files, host.Clock).Measure(targets, AgentWalk.MeasureNowBudget, withNames: true, cancellationToken);
        return (new AgentSizes.Now(sample), last.Agents.Map(a => a.Value).ValueOr(null!));
    }

    private static (AgentSizes, AgentsSample?) Recorded(LastSlowParts last) => last.Agents switch
    {
        Reading<AgedPart<AgentsSample>>.Available { Value: var aged } => (new AgentSizes.FullRun(aged), last.PreviousAgents.ValueOr(null!)),
        var missing => (new AgentSizes.None($"{missing.ReasonOrEmpty}; agents list --measure walks the folders now"), null),
    };

    private static string Render(AgentsReport report)
    {
        var text = new StringBuilder().AppendLine($"wsl-care agents ({report.Side}) — sizes: {Source(report.Sizes)}");
        foreach (var agent in report.Agents.Where(a => a.Tracked))
        {
            var size = agent.TotalBytes.Available ? $"{agent.TotalBytes.Bytes / 1048576.0:0.0} MiB" : "—";
            var sessions = agent.Sessions.Counted ? agent.Sessions.Count!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "—";
            text.AppendLine(CommandLine.Printable($"  {agent.Name,-20} {size,12}  sessions {sessions,6}  by {string.Join("+", agent.DetectedBy)}{Version(agent)}"));
        }

        var untracked = report.Agents.Count(a => !a.Tracked);
        return text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{untracked} more catalogue agent(s) not found here").ToString();
    }

    private static string Version(AgentReport agent) => agent.Version.Available ? $"  {agent.Version.Value}" : string.Empty;

    private static string Source(AgentSizesSource sizes) => sizes.Source switch
    {
        "now" => "measured now",
        "fullRun" => $"the full run {sizes.RunId}, {sizes.AgeSeconds / 3600:0.0} h ago",
        _ => sizes.Reason ?? "none",
    };
}
