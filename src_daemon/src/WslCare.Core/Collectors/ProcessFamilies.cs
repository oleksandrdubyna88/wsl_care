using System.Text.RegularExpressions;

namespace WslCare.Core.Collectors;

/// <summary>A named group of processes the report aggregates (plan §4.2).</summary>
/// <param name="Name">The name the report and the A11 allowlist (<c>processes.families</c>) use.</param>
/// <param name="Pattern">Matched against the process's argv joined by spaces — the whole of it, or only
/// the program and its first argument when <paramref name="HeadOnly"/> (an agent or a node script is
/// recognised by what runs, not by a path that merely appears among its arguments).</param>
/// <param name="HeadOnly">Match the first two arguments only.</param>
public sealed record ProcessFamily(string Name, Regex Pattern, bool HeadOnly)
{
    public bool Matches(IReadOnlyList<string> argv, string name)
    {
        var subject = HeadOnly ? string.Join(' ', argv.Take(2)) : string.Join(' ', argv);
        return Pattern.IsMatch(subject.Length > 0 ? subject : name);
    }
}

/// <summary>
/// The family catalogue of plan §4.2, first match wins. Built in: the configuration schema holds
/// scalars and string lists only, so a regex-per-family setting waits for the object-shaped keys E7
/// brings (as <c>aiAgents.extra</c> does).
/// </summary>
/// <remarks>The order is the decision: a Claude Code binary shipped inside a VS Code extension is an AI
/// agent, not "vscode-server"; an MSBuild node is a build server even when VS Code started it.</remarks>
public static partial class ProcessFamilies
{
    public const string Other = "other";

    /// <summary>The family name of the AI agents' CLIs — never choosable for A11 (§15q Q13: their processes are the owner's work).</summary>
    public const string AiAgents = "ai-agents";

    /// <summary>The C# language server (E14 S3) — a family of its own, before <c>vscode-server</c>, so A11 can name it without
    /// naming the VS Code server itself (daemonised, parent 1, no terminal: an A11 suspect at an idle desk — coai plan round
    /// 2026-10-08, finding 5).</summary>
    public const string LanguageServers = "language-servers";

    /// <summary>The .NET build servers (A3's family; the CPU history records them since E14 S3) — named here, in the catalogue,
    /// so the history does not depend on the action that reads it (coai code round 2026-10-08, finding 1).</summary>
    public const string DotnetBuildServers = "dotnet-build-servers";

    public static readonly IReadOnlyList<ProcessFamily> Catalogue =
    [
        new(DotnetBuildServers, BuildServers(), HeadOnly: false),
        new("testhost", TestHost(), HeadOnly: false),
        new(AiAgents, AiAgentCli(), HeadOnly: true),
        new("docker-desktop-proxy", DockerDesktopProxy(), HeadOnly: true),
        new(LanguageServers, LanguageServer(), HeadOnly: false),
        new("vscode-server", VsCodeServer(), HeadOnly: false),
        new("node", Node(), HeadOnly: true),
    ];

    /// <summary>The families <c>processes.families</c> may name (§15q R1.3, review B1): the catalogue's, without the catch-all
    /// <see cref="Other"/> (every process nothing else matched — root's A11 would end other accounts' idle orphans) and without
    /// <see cref="AiAgents"/>. Declared after <see cref="Catalogue"/>: static initialisers run in textual order.</summary>
    public static readonly IReadOnlyList<string> ChoosableForA11 =
        [.. Catalogue.Select(f => f.Name).Where(name => name is not (Other or AiAgents))];

    /// <summary>The family of one process; <see cref="Other"/> when none matches.</summary>
    public static string Of(IReadOnlyList<string> argv, string name) =>
        Catalogue.FirstOrDefault(f => f.Matches(argv, name))?.Name ?? Other;

    /// <summary>MSBuild worker nodes (<c>MSBuild.dll /nodemode:</c>), the Roslyn compiler server, the Razor server.</summary>
    [GeneratedRegex(@"MSBuild(?:\.dll)?\b.*\s/nodemode:|\bVBCSCompiler\b|\brzc(?:\.dll)?\b", RegexOptions.CultureInvariant)]
    private static partial Regex BuildServers();

    [GeneratedRegex(@"\btesthost(?:\.[a-z0-9]+)*(?:\.dll|\.exe)?\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TestHost();

    /// <summary>The AI-agent CLIs of plan §4.6 by binary name — the program itself, or the script node runs.</summary>
    [GeneratedRegex(@"(?:^|[/ ])(?:claude|codex|gemini|agy|antigravity|copilot|acli|rovodev|atlassian_cli_rovodev|ollama)(?:\.exe)?(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex AiAgentCli();

    [GeneratedRegex(@"docker-desktop-user-distro|docker-desktop-proxy", RegexOptions.CultureInvariant)]
    private static partial Regex DockerDesktopProxy();

    /// <summary>The Roslyn language server C# Dev Kit runs (measured in the captured 2026-10-02 tree): the PROGRAM itself, or the
    /// assembly <c>dotnet</c> runs — <c>dotnet [exec] [--option value]… …/Microsoft.CodeAnalysis.LanguageServer.dll</c> (coai code
    /// round 2026-10-08, findings 3, 6, 7: <c>exec</c> and its options put the assembly past the first two words). Anchored at
    /// the program, so a process that only names the server among its arguments is not one.</summary>
    [GeneratedRegex(@"^(?:\S*/)?(?:Microsoft\.CodeAnalysis\.LanguageServer(?:\.exe)?|dotnet(?:\.exe)?\s+(?:exec\s+)?(?:-\S*\s+\S+\s+)*\S*Microsoft\.CodeAnalysis\.LanguageServer\.dll)(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageServer();

    /// <summary>Everything else VS Code runs in the distro: the server, extension hosts, ServiceHub (the C# language server is
    /// matched first, as <see cref="LanguageServers"/>; a launch form that rule does not know still lands here by name — the
    /// family it had before E14 S3).</summary>
    [GeneratedRegex(@"/\.vscode-server/|\bServiceHub\b|Microsoft\.CodeAnalysis\.LanguageServer|Microsoft\.VisualStudio\.Code\.", RegexOptions.CultureInvariant)]
    private static partial Regex VsCodeServer();

    [GeneratedRegex(@"(?:^|/)node(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex Node();
}
