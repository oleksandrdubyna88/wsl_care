namespace WslCare.Core.Mcp;

/// <summary>Where an MCP server writes the logs its activity and its starts are read from (plan §15q E7.S2d) — a closed set: a
/// server with another layout adds its own case here, never an <c>if</c> on a server's name.</summary>
public abstract record McpLogLayout
{
    private McpLogLayout()
    {
    }

    /// <summary>No layout is known: activity is not derivable, starts fall back to the live instances younger than the window.</summary>
    public sealed record None : McpLogLayout;

    /// <summary>The family's logging contract: <c>{root}/{yyyy-MM-dd}/{prefix}-{HH-mm-ss}-{pid}.log</c>, UTC, a new file per run, a
    /// run that outlives the day continuing in a <c>00-00-00</c> file of the same pid under the next day's folder.</summary>
    /// <param name="UnderHome">The log root, relative to the home (<c>.local/share/coai-mcp/logs</c>).</param>
    /// <param name="Prefix">The file names' prefix — the application name the host logs as.</param>
    public sealed record FamilyRunLogs(string UnderHome, string Prefix) : McpLogLayout;
}

/// <summary>One MCP server the daemon recognises.</summary>
/// <param name="Name">The name <c>mcpServers.watched</c> lists and the report shows.</param>
/// <param name="Programs">The PROGRAM file names (argv[0], <c>.exe</c> stripped) that are this server — never an argument of
/// another program (plan §15q E7.S2d C-2: <c>printf coai-mcp</c> is not a server).</param>
public sealed record McpServerEntry(string Name, IReadOnlyList<string> Programs, McpLogLayout Logs);

/// <summary>
/// The MCP servers of AI agents the daemon watches (plan §15q E7.S2d) — embedded, a closed list; which of them are watched is
/// the configuration key <c>mcpServers.watched</c>, closed over these names (the E7.S0 B1 rule: no list key takes free text).
/// </summary>
public static class McpServerCatalogue
{
    /// <summary>The ConnectOtherAIs MCP server: one stdio process per agent session, measured 2026-10-06 under the Claude Code
    /// extension's native <c>claude</c> binary, logging per the family contract under <c>~/.local/share/coai-mcp/logs</c>.</summary>
    public static readonly McpServerEntry CoaiMcp = new("coai-mcp", ["coai-mcp"], new McpLogLayout.FamilyRunLogs(".local/share/coai-mcp/logs", "coai-mcp"));

    public static IReadOnlyList<McpServerEntry> Servers { get; } = [CoaiMcp];

    /// <summary>Every name, in catalogue order — the allowed set of <c>mcpServers.watched</c>.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. Servers.Select(s => s.Name)];
}
