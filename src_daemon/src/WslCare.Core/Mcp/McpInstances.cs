using WslCare.Core.Agents;
using WslCare.Core.Collectors;

namespace WslCare.Core.Mcp;

/// <summary>A process of a watched server, with its owner.</summary>
public sealed record McpFound(ProcessEntry Process, McpServerEntry Server, McpOwner Owner);

/// <summary>What one snapshot holds: the instances, and how many server processes were under a live process that is no agent.</summary>
public sealed record McpFinding(IReadOnlyList<McpFound> Instances, int NotUnderAgent);

/// <summary>
/// The MCP server instances of the AI agents in ONE process snapshot (plan §15q E7.S2d, Decided 1) — no second <c>/proc</c> walk:
/// a process whose PROGRAM (argv[0], from the raw argv) is a watched server's, owned by the first ancestor that is a catalogue
/// agent (<see cref="AgentProcesses.AgentOf"/>, the attribution A18 uses), or orphaned when it was re-parented with no agent
/// above it. One under a live process that is no agent is not an instance.
/// </summary>
public static class McpInstances
{
    public static McpFinding Find(IReadOnlyList<ProcessEntry> processes, IReadOnlyList<McpServerEntry> watched)
    {
        var byPid = processes.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First());
        var matched = processes
            .Select(p => (Process: p, Server: ServerOf(p, watched)))
            .Where(m => m.Server is not null)
            .Select(m => (m.Process, Server: m.Server!, Owner: OwnerOf(m.Process, byPid)))
            .ToList();
        return new McpFinding(
            [.. matched.Where(m => m.Owner is not null).Select(m => new McpFound(m.Process, m.Server, m.Owner!)).OrderBy(f => f.Process.Pid)],
            matched.Count(m => m.Owner is null));
    }

    /// <summary>The watched server whose program the process runs — or, for an interpreter-run server (E14 S2d), whose SCRIPT its
    /// interpreter runs; <c>null</c> for none. The program or the script only — a server's name as a later argument of another
    /// program is not that server (consultation C-2).</summary>
    public static McpServerEntry? ServerOf(ProcessEntry process, IReadOnlyList<McpServerEntry> watched) =>
        process.Programs.FirstOrDefault() is { Length: > 0 } program
            ? watched.FirstOrDefault(s => s.Programs.Contains(program, StringComparer.Ordinal) || s.RunAs.Any(form => Runs(form, program, process.Script)))
            : null;

    /// <summary>The program is one of the form's interpreters and runs one of its scripts from under one of its folders.</summary>
    private static bool Runs(McpScriptMatch form, string program, string script) =>
        script.Length > 0
        && form.Interpreters.Contains(program, StringComparer.Ordinal)
        && form.Scripts.Contains(Collectors.Procfs.CommandLineText.FileNameOf(script), StringComparer.Ordinal)
        && form.Under.Any(folder => script.Replace('\\', '/').Contains(folder, StringComparison.Ordinal));

    /// <summary>The first ancestor that is a catalogue agent; <see cref="McpOwner.Orphaned"/> when there is none and the process was
    /// re-parented (the product's own orphan rule, <see cref="ProcessEntry.Orphaned"/>); <c>null</c> when it runs under a live
    /// process that is no agent. A visited set ends any loop a torn snapshot could hold.</summary>
    public static McpOwner? OwnerOf(ProcessEntry process, IReadOnlyDictionary<int, ProcessEntry> byPid)
    {
        var visited = new HashSet<int> { process.Pid };
        for (var at = Parent(process, byPid); at is not null && visited.Add(at.Pid); at = Parent(at, byPid))
        {
            if (AgentAt(at) is { } agent)
            {
                return agent;
            }
        }

        return process.Orphaned ? new McpOwner.Orphaned() : null;
    }

    private static McpOwner.Agent? AgentAt(ProcessEntry process) =>
        process.Family == ProcessFamilies.AiAgents && AgentProcesses.AgentOf(process) is { } entry
            ? new McpOwner.Agent(process.Pid, entry.Name, process.CommandLine)
            : null;

    private static ProcessEntry? Parent(ProcessEntry process, IReadOnlyDictionary<int, ProcessEntry> byPid) =>
        byPid.GetValueOrDefault(process.ParentPid);
}
