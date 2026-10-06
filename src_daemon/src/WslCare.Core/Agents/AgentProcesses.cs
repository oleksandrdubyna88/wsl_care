using WslCare.Core.Collectors;

namespace WslCare.Core.Agents;

/// <summary>
/// Which catalogue agent a process IS — the one attribution A18 (<see cref="Actions.Suspects.AgentOrphans"/>) and the MCP
/// servers' owner walk (plan §15q E7.S2d) share: extracted from A18, one function, never a second copy.
/// </summary>
public static class AgentProcesses
{
    /// <summary>The ONE catalogue agent whose binary the process runs (its program, or the script node runs — the RAW argv's
    /// <see cref="ProcessEntry.Programs"/>, plan §15q E7.S2d C-2); <c>null</c> for none or more than one.</summary>
    public static AgentEntry? AgentOf(ProcessEntry process)
    {
        var agents = AgentCatalogue.Agents.Where(a => a.Binaries.Any(b => process.Programs.Contains(b, StringComparer.Ordinal))).ToList();
        return agents.Count == 1 ? agents[0] : null;
    }
}
