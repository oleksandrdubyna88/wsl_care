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

/// <summary>Where a watched server's entry comes from (plan E14 S2c).</summary>
public enum McpServerOrigin
{
    /// <summary>The embedded catalogue: a name this product chose, specific to that server.</summary>
    Catalogue,

    /// <summary>A program file name the user listed in <c>mcpServers.programs</c>: once no agent holds such a process, it may be
    /// an unrelated program of the same name.</summary>
    UserProgram,
}

/// <summary>One MCP server the daemon recognises.</summary>
/// <param name="Name">The name <c>mcpServers.watched</c> (or <c>mcpServers.programs</c>) lists and the report shows.</param>
/// <param name="Programs">The PROGRAM file names (argv[0], <c>.exe</c> stripped) that are this server — never an argument of
/// another program (plan §15q E7.S2d C-2: <c>printf coai-mcp</c> is not a server).</param>
/// <param name="Origin">The catalogue's, or the user's own (plan E14 S2c).</param>
public sealed record McpServerEntry(string Name, IReadOnlyList<string> Programs, McpLogLayout Logs, McpServerOrigin Origin = McpServerOrigin.Catalogue)
{
    /// <summary>The interpreter-run forms of this server (E14 S2d): a process whose program is one of the interpreters and whose
    /// SCRIPT is one of the scripts, under one of the folders. Empty for a server that is its own program.</summary>
    public IReadOnlyList<McpScriptMatch> RunAs { get; init; } = [];
}

/// <summary>One interpreter-run form of a server (E14 S2d): <paramref name="Interpreters"/> — program file names, as
/// <see cref="Collectors.ProcessEntry.Programs"/> compares them — running a script whose file name is one of
/// <paramref name="Scripts"/> and whose path holds one of <paramref name="Under"/> (an installed package's folder), so a script
/// of that name elsewhere is not the server.</summary>
public sealed record McpScriptMatch(IReadOnlyList<string> Interpreters, IReadOnlyList<string> Scripts, IReadOnlyList<string> Under);

/// <summary>
/// The MCP servers of AI agents the daemon watches (plan §15q E7.S2d) — embedded, a closed list; which of them are watched is
/// the configuration key <c>mcpServers.watched</c>, closed over these names (the E7.S0 B1 rule: no list key takes free text).
/// </summary>
public static class McpServerCatalogue
{
    /// <summary>The ConnectOtherAIs MCP server: one stdio process per agent session, measured 2026-10-06 under the Claude Code
    /// extension's native <c>claude</c> binary, logging per the family contract under <c>~/.local/share/coai-mcp/logs</c>.</summary>
    public static readonly McpServerEntry CoaiMcp = new("coai-mcp", ["coai-mcp"], new McpLogLayout.FamilyRunLogs(".local/share/coai-mcp/logs", "coai-mcp"));

    /// <summary>The Playwright MCP server, as <c>npx @playwright/mcp</c> starts it (E14 S2d, the captured 2026-10-02 tree twice):
    /// <c>npm exec</c> → a shell → <c>node …/node_modules/.bin/playwright-mcp</c> — the node process is the server, its launchers
    /// are not. A script run through its shebang is <c>node</c> too, so the server is never its own program here. No log layout.</summary>
    public static readonly McpServerEntry PlaywrightMcp = new("playwright-mcp", [], new McpLogLayout.None())
    {
        // coai code round 2026-10-08, finding 5: the package's bin link or the package itself — never a same-named file in
        // another package.
        RunAs = [new McpScriptMatch(["node", "nodejs"], ["playwright-mcp"], ["/node_modules/.bin/", "/node_modules/@playwright/mcp/"])],
    };

    public static IReadOnlyList<McpServerEntry> Servers { get; } = [CoaiMcp, PlaywrightMcp];

    /// <summary>Every name, in catalogue order — the allowed set of <c>mcpServers.watched</c>.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. Servers.Select(s => s.Name)];
}
