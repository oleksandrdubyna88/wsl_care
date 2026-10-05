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
        IReadOnlyList<AgentPresence> found =
        [
            .. AgentDiscovery.Discover(host.Paths, host.Files, Environment.GetEnvironmentVariable("PATH"), host.Privilege.IsRoot),
            .. ExtraAgents.Discover(host.Paths, host.Files, loaded.Config, host.Actions),
        ];
        var last = LastFullRun.Read(host.Paths, host.Files, host.Clock);
        var (sizes, previous) = request.Measure
            ? Measured(host, found, last, cancellationToken)
            : Recorded(last);
        var report = AgentsReports.From(host.Paths.Side == Core.Hosting.HostSide.Wsl ? "wsl" : "windows", now, found, sizes, previous, loaded.Config);
        return Output.Answer(stdout, request.Json ? JsonSerializer.Serialize(report, WslCareJsonContext.Default.AgentsReport) : Render(report));
    }

    /// <summary>Plan §15q D4, review C3: the fix a root probe names.</summary>
    internal const string RootRefusal =
        "agents probe runs as the user who owns the CLI, not as uid 0: set the distribution's default user (wsl.exe --manage <distro> --set-default-user <user>, or [user] default=<user> in /etc/wsl.conf) and ask again. Nothing was looked at.";

    /// <summary><c>agents probe &lt;path&gt; [--json]</c> (plan §15q D4): refused as root (uid 0, naming the fix); on the Windows
    /// binary not built yet (E7.S5b); otherwise the probe of <see cref="AgentProbe"/> — read-only, nothing executed.</summary>
    public static int Probe(Request.AgentsProbe request, CliHost host, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        if (host.Privilege.IsRoot)
        {
            Output.Note(stderr, RootRefusal);
            return (int)ExitCode.NotAsRoot;
        }

        if (host.Paths is not Core.Hosting.LinuxHostPaths linux)
        {
            return Output.Refuse(stderr, "agents probe of a Windows path arrives with the Windows agents (E7.S5b); this binary probes the distro's CLIs only.");
        }

        var report = AgentProbe.Probe(linux, host.Files, request.Path, host.Clock, cancellationToken);
        return Output.Answer(stdout, request.Json ? JsonSerializer.Serialize(report, WslCareJsonContext.Default.AgentProbeReport) : RenderProbe(report));
    }

    private static string RenderProbe(AgentProbeReport report)
    {
        var text = new StringBuilder().AppendLine(CommandLine.Printable($"{report.Path}: {(report.Usable ? "a CLI this user may start" : report.Reason)}{(report.Link ? " (a link)" : string.Empty)}"));
        text.AppendLine(CommandLine.Printable(report.TrackedAs.Length > 0 ? $"  already tracked as {report.TrackedAs}" : $"  name: {report.Name}"));
        foreach (var folder in report.DataFolders)
        {
            var size = folder.Folder.Size.Available ? $"{folder.Folder.Size.Bytes / 1048576.0:0.0} MiB" : "—";
            text.AppendLine(CommandLine.Printable($"  {folder.Folder.Path} {size}{(folder.Refusal.Length > 0 ? $" — {folder.Refusal}" : string.Empty)}"));
        }

        return text.Append(report.Suggested is null ? "  nothing to add" : "  could be added as a manual agent").ToString();
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
