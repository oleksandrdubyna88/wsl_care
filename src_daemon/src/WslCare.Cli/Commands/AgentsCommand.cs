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
    public static int Run(Request.AgentsList request, CliHost host, ConfigLoadResult loaded, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var now = host.Clock.GetUtcNow();
        IReadOnlyList<AgentPresence> found =
        [
            .. AgentDiscovery.Discover(host.Paths, host.Files, Environment.GetEnvironmentVariable("PATH"), host.Privilege.IsRoot),
            .. ExtraAgents.Discover(host.Paths, host.Files, loaded.Config, host.Actions),
        ];
        var last = LastFullRun.Read(host.Paths, host.Files, host.Clock);
        var (sizes, previous) = request.Measure
            ? Measured(host, found, last, stderr, cancellationToken)
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
    /// <remarks>coai E7 code round #7: the walk may take its whole budget, so it says on STDERR what it is about to do and each
    /// folder it starts — stdout (the JSON) is untouched.</remarks>
    private static (AgentSizes, AgentsSample?) Measured(CliHost host, IReadOnlyList<AgentPresence> found, LastSlowParts last, TextWriter stderr, CancellationToken cancellationToken)
    {
        var targets = found.Where(p => p.Tracked).Select(p => p.Target).ToList();
        Output.Note(stderr, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"measuring {targets.Sum(t => t.Folders.Count)} agent folder(s), up to {AgentWalk.MeasureNowBudget.TotalSeconds:0} s…"));
        var walk = new AgentWalk(host.Files, host.Clock, host.Paths.Home) { OnFolder = folder => Output.Note(stderr, CommandLine.Printable($"measuring {folder}")) };
        var sample = walk.Measure(targets, AgentWalk.MeasureNowBudget, withNames: true, cancellationToken);
        return (new AgentSizes.Now(sample), Sample(last.Agents.Map(a => a.Value)));
    }

    private static (AgentSizes, AgentsSample?) Recorded(LastSlowParts last) => last.Agents switch
    {
        Reading<AgedPart<AgentsSample>>.Available { Value: var aged } => (new AgentSizes.FullRun(aged), Sample(last.PreviousAgents)),
        var missing => (new AgentSizes.None($"{missing.ReasonOrEmpty}; agents list --measure walks the folders now"), null),
    };

    /// <summary>A recorded sample, or none: the report's own "not found" (<c>AgentsSample?</c>), spelt as such — never a
    /// <c>null!</c> forced through a non-nullable fallback (retro gate over PR #17).</summary>
    private static AgentsSample? Sample(Reading<AgentsSample> recorded) => recorded is Reading<AgentsSample>.Available { Value: var sample } ? sample : null;

    private static string Render(AgentsReport report)
    {
        var text = new StringBuilder().AppendLine($"wsl-care agents ({report.Side}) — sizes: {Source(report.Sizes)}");
        foreach (var agent in report.Agents.Where(a => a.Tracked))
        {
            var size = agent.TotalBytes.Available ? $"{agent.TotalBytes.Bytes / 1048576.0:0.0} MiB" : "—";
            var sessions = agent.Sessions.View() switch
            {
                SessionCount.Counted counted => counted.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => "—",
            };
            text.AppendLine(CommandLine.Printable($"  {agent.Name,-20} {size,12}  sessions {sessions,6}  by {string.Join("+", agent.DetectedBy)}{Version(agent)}"));
        }

        var untracked = report.Agents.Count(a => !a.Tracked);
        return text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{untracked} more catalogue agent(s) not found here").ToString();
    }

    private static string Version(AgentReport agent) => agent.Version.Available ? $"  {agent.Version.Value}" : string.Empty;

    private static string Source(AgentSizesSource sizes) => sizes.View() switch
    {
        SizesView.MeasuredNow => "measured now",
        SizesView.FullRun run => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"the full run {run.RunId}, {run.AgeSeconds / 3600:0.0} h ago"),
        SizesView.Unavailable none => none.Reason,
        _ => throw new System.Diagnostics.UnreachableException("SizesView is a closed set"),
    };
}
