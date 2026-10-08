using System.Text.RegularExpressions;

using WslCare.Core.Agents;

namespace WslCare.Core.Mcp;

/// <summary>
/// The MCP servers a USER adds to the watched list by program file name — <c>mcpServers.programs</c> (plan E14 S2c, the owner's
/// Q-M2 of 2026-10-08). An open list, never free text (plan §15q R1.3): a member is a FILE NAME (what argv[0] ends in, as
/// <see cref="Collectors.Procfs.CommandLineText.ProgramNames"/> compares it) and is refused when it names an AI agent's program, an
/// interpreter, shell or launcher (argv[0] of every script it runs is that name, so it would make every such process a server),
/// this product, or a catalogue server. Each added program is a server with no known log layout.
/// </summary>
public static partial class McpUserPrograms
{
    /// <summary>The most programs the list may hold.</summary>
    public const int MaxMembers = 32;

    /// <summary>A file name: a letter or digit, then letters, digits, <c>. _ + -</c> — no path, no space, at most 64 characters.
    /// JavaScript-compatible, for the extension's schema.</summary>
    public const string Pattern = "^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$";

    public const string Description = "a program file name (a letter or digit, then letters, digits, . _ + -; at most 64 characters, no path)";

    public const string NotAFileName = "it is not " + Description;
    public const string WithExe = "write it without .exe: the daemon strips .exe before it compares";
    public const string AnAgent = "it is an AI agent's program: an agent is never an MCP server";
    public const string ALauncher = "it is an interpreter, shell or launcher: every script it runs has this argv[0], so every such process would count as a server; an interpreter-run server needs a catalogue entry naming its script";
    public const string ThisProduct = "it is this product";
    public const string ACatalogueServer = "it is a catalogue server: choose it in mcpServers.watched";

    /// <summary>The product's own program.</summary>
    private const string Product = "wsl-care";

    /// <summary>Interpreters, shells and launchers: the argv[0] of the programs they start. A version suffix (<c>python3.12</c>,
    /// <c>node20</c>) is the same launcher.</summary>
    public static IReadOnlyList<string> Launchers { get; } =
    [
        "node", "nodejs", "deno", "bun", "tsx", "ts-node", "python", "python3", "pypy", "pypy3", "uv", "uvx", "pip", "pipx",
        "poetry", "conda", "npx", "npm", "pnpm", "yarn", "sh", "bash", "dash", "zsh", "fish", "ksh", "busybox", "env", "sudo", "su",
        "nohup", "setsid", "nice", "ionice", "timeout", "stdbuf", "xargs", "tmux", "screen", "dotnet", "java", "go", "cargo",
        "ruby", "perl", "php", "docker", "podman", "ssh", "sshd", "systemd", "init", "login", "cron",
    ];

    /// <summary>Every name refused outright (the version-suffixed launchers aside): the agents' programs, the launchers, this
    /// product and the catalogue servers — what the contract lists.</summary>
    public static IReadOnlyList<string> Refused { get; } =
        [.. AgentPrograms().Concat(Launchers).Append(Product).Concat(CataloguePrograms()).Distinct(StringComparer.Ordinal)];

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex FileName();

    /// <summary>Why <paramref name="name"/> cannot be a user program; empty when it can. The shape first, then what it names.</summary>
    public static string Problem(string name) => IsFileName(name) ? Names(name) : NotAFileName;

    /// <summary>The WHOLE value matches: .NET's <c>$</c> also matches before a final newline (E7.S0 review S5).</summary>
    private static bool IsFileName(string name) => FileName().Match(name) is { Success: true } m && m.Index == 0 && m.Length == name.Length;

    /// <summary>The servers the user listed — each once, a refused name skipped (the layers were validated at load; this is the
    /// reader's own guard) — each with no known log layout.</summary>
    public static IReadOnlyList<McpServerEntry> Entries(IReadOnlyList<string> names) =>
        [.. names.Distinct(StringComparer.Ordinal).Where(n => Problem(n).Length == 0).Select(n => new McpServerEntry(n, [n], new McpLogLayout.None(), McpServerOrigin.UserProgram))];

    /// <summary>What a well-formed name must not be, in the order a refusal names it.</summary>
    private static readonly (Func<string, bool> Is, string Why)[] Refusals =
    [
        (n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase), WithExe),
        (n => AgentPrograms().Contains(n, StringComparer.Ordinal), AnAgent),
        (IsLauncher, ALauncher),
        (n => string.Equals(n, Product, StringComparison.Ordinal), ThisProduct),
        (n => CataloguePrograms().Contains(n, StringComparer.Ordinal), ACatalogueServer),
    ];

    private static string Names(string name) => Refusals.Where(r => r.Is(name)).Select(r => r.Why).FirstOrDefault(string.Empty);

    /// <summary>A launcher, or one with a version suffix (<c>python3.12</c>, <c>node20</c>).</summary>
    private static bool IsLauncher(string name) =>
        Launchers.Contains(name, StringComparer.Ordinal) || Launchers.Contains(name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.'), StringComparer.Ordinal);

    private static IEnumerable<string> AgentPrograms() => AgentCatalogue.Agents.SelectMany(a => a.Binaries);

    private static IEnumerable<string> CataloguePrograms() => McpServerCatalogue.Servers.SelectMany(s => s.Programs.Append(s.Name));
}
