using WslCare.Core.Agents;
using WslCare.Core.Config;

namespace WslCare.Core.Mcp;

/// <summary>
/// The MCP servers the Windows binary counts (E14 S7a): the catalogue's <c>coai-mcp</c> when <c>mcpServers.watched</c> holds it,
/// <see cref="CredsMcp"/>, and the user's <c>mcpServers.programs</c>. A Windows process is one of them when its exe name, <c>.exe</c>
/// stripped, is one of the server's program names compared WITHOUT case (Windows file names are case-insensitive).
/// </summary>
public static class WindowsMcpCatalogue
{
    /// <summary>The CredsForDevs MCP server, catalogued on Windows only: the W9 measurement (2026-10-07) found 85 <c>creds-mcp.exe</c>,
    /// 66 of them orphaned, 1.32 GB; 2026-10-09 found 35 under one VS Code WSL connection. On the distro it stays a user-program
    /// choice (plan E14 S2c). No log layout is read on Windows.</summary>
    public static readonly McpServerEntry CredsMcp = new("creds-mcp", ["creds-mcp"], new McpLogLayout.None());

    /// <summary>The servers this configuration has the Windows binary count, in order; the first that matches a process names it.</summary>
    public static IReadOnlyList<McpServerEntry> Servers(EffectiveConfig config) =>
    [
        .. McpServerCatalogue.Servers.Where(s => s.Programs.Count > 0 && config.TextList(ConfigKeys.McpServers.Watched).Contains(s.Name, StringComparer.Ordinal)),
        CredsMcp,
        .. McpUserPrograms.Entries(config.TextList(ConfigKeys.McpServers.Programs)),
    ];

    /// <summary>The server <paramref name="exeName"/> runs, or <c>null</c>.</summary>
    public static McpServerEntry? ServerOf(string exeName, IReadOnlyList<McpServerEntry> servers)
    {
        var program = ProgramOf(exeName);
        return program.Length == 0 ? null : servers.FirstOrDefault(s => s.Programs.Contains(program, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Whether <paramref name="exeName"/> is a catalogue agent's binary (<c>claude.exe</c> …).</summary>
    public static bool IsAgent(string exeName) =>
        AgentCatalogue.Agents.Any(a => a.Binaries.Contains(ProgramOf(exeName), StringComparer.OrdinalIgnoreCase));

    /// <summary>The WSL launcher: a process under it is a WSL connection's interop child.</summary>
    public static bool IsWsl(string exeName) => string.Equals(ProgramOf(exeName), "wsl", StringComparison.OrdinalIgnoreCase);

    /// <summary>An exe name without its <c>.exe</c> (any case).</summary>
    public static string ProgramOf(string exeName) =>
        exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exeName[..^4] : exeName;
}
