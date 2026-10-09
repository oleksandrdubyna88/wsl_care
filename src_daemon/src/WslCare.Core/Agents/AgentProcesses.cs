using WslCare.Core.Collectors;

namespace WslCare.Core.Agents;

/// <summary>
/// Which catalogue agent a process IS — the one attribution A18 (<see cref="Actions.Suspects.AgentOrphans"/>), the MCP
/// servers' owner walk (plan §15q E7.S2d) and the archive's in-use check (plan §15r E9.S1) share: extracted from A18, one
/// function, never a second copy.
/// </summary>
public static class AgentProcesses
{
    /// <summary>The ONE catalogue agent whose binary the process runs (its program, or the script node runs — the RAW argv's
    /// <see cref="ProcessEntry.Programs"/>, plan §15q E7.S2d C-2); <c>null</c> for none or more than one.</summary>
    public static AgentEntry? AgentOf(ProcessEntry process) => AgentOfPrograms(process.Programs);

    /// <summary>The same over the program names of a raw argv (<see cref="Collectors.Procfs.CommandLineText.ProgramNames"/>) —
    /// what the archive's in-use check reads from <c>/proc/&lt;pid&gt;/cmdline</c>.</summary>
    public static AgentEntry? AgentOfPrograms(IReadOnlyList<string> programs)
    {
        var agents = AgentCatalogue.Agents.Where(a => a.Binaries.Any(b => programs.Contains(b, StringComparer.Ordinal))).ToList();
        return agents.Count == 1 ? agents[0] : null;
    }
}
