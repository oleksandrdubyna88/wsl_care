using System.Text.Json;

using WslCare.Core.Hosting;
using WslCare.Core.Json;

namespace WslCare.Core.Agents;

/// <summary>Where an agent's sessions are and what one session is (plan §15q D2): a glob of name patterns, one per folder
/// level, under one of its data folders — matched over directory listings alone, never by opening a file.</summary>
/// <param name="LinuxUnder">The data folder (catalogue spelling, <c>~/…</c>) the glob starts in; empty = not counted on Linux.</param>
/// <param name="WindowsUnder">The same on Windows (<c>%USERPROFILE%\…</c>); empty = not counted on Windows.</param>
/// <param name="Glob">Name patterns separated by <c>/</c> (<c>*</c> and <c>?</c> within one name); the last matches files.</param>
/// <param name="Source">Where the layout was confirmed.</param>
/// <param name="Companions">D2's one session beyond its transcript (review R7): files and folders relative to the layout's
/// folder, <c>{dir}</c> the session file's folder and <c>{id}</c> its name without the extension — sized with the session.</param>
public sealed record AgentSessionLayout(string LinuxUnder, string WindowsUnder, string Glob, string Source, IReadOnlyList<string> Companions);

/// <summary>One agent of the catalogue (plan §4.6, §15q): data, not code.</summary>
/// <param name="Linux">Data folders inside the distro, <c>~/…</c> (the TARGET user's home as root).</param>
/// <param name="Windows">Data folders on the Windows side, from <c>%USERPROFILE%</c>, <c>%LOCALAPPDATA%</c> or <c>%APPDATA%</c>.</param>
/// <param name="NeverEnter">Folder names its walk never enters (<c>memory</c> is never entered for ANY agent, whatever this says).</param>
/// <param name="NeverEnterPrefixes">Folder-name prefixes its walk never enters (another agent's folder inside this one).</param>
/// <param name="Sessions">The confirmed session layout; <c>null</c> = monitor only, sessions not counted (shown "—", never 0).</param>
/// <param name="VersionInLinkTarget">A regex with a group <c>v</c> over the binary's link target (a native install encodes its
/// version in it); empty = none. Nothing is ever executed to ask a version (plan §15q D3).</param>
/// <param name="Confirmed">Its folders were seen on this owner's machines (2026-10-02); otherwise taken from its documentation.</param>
public sealed record AgentEntry(
    string Id,
    string Name,
    IReadOnlyList<string> Binaries,
    IReadOnlyList<string> NpmPackages,
    IReadOnlyList<string> Linux,
    IReadOnlyList<string> Windows,
    IReadOnlyList<string> NeverEnter,
    IReadOnlyList<string> NeverEnterPrefixes,
    AgentSessionLayout? Sessions,
    string VersionInLinkTarget,
    bool Confirmed);

/// <summary>The embedded <c>agents.json</c>.</summary>
public sealed record AgentCatalogueFile(int SchemaVersion, IReadOnlyList<AgentEntry> Agents);

/// <summary>
/// The agent catalogue (plan §4.6, §15q E7.S1): the ONE list of known AI agents, their binaries, npm packages, data folders per
/// side and session layouts. It feeds discovery, the daily walk and — the half that is a safety rule — the protected roots
/// (<see cref="LinuxHostPaths.AgentRoots"/>, <see cref="WindowsHostPaths.AgentRoots"/>): nothing under any catalogue folder is
/// ever deleted (plan §15q H1).
/// </summary>
public static class AgentCatalogue
{
    public const string Resource = "WslCare.Core.Agents.agents.json";

    /// <summary>The folder name EVERY agent walk declines to enter (plan §15q H2): an agent's long-term memory.</summary>
    public const string Memory = "memory";

    private static readonly Lazy<IReadOnlyList<AgentEntry>> Embedded = new(Load);

    /// <summary>Every agent this build knows, in catalogue order.</summary>
    public static IReadOnlyList<AgentEntry> Agents => Embedded.Value;

    /// <summary>A catalogue folder spelt <c>~/…</c> as a path under <paramref name="home"/>.</summary>
    public static string LinuxFolder(string spelt, string home) =>
        spelt == "~" ? home : PathRules.Linux.Join(home, [.. spelt[2..].Split('/', StringSplitOptions.RemoveEmptyEntries)]);

    /// <summary>A catalogue folder spelt <c>%ROOT%\…</c> as a path under the matching Windows folder; empty for an unknown root.</summary>
    public static string WindowsFolder(string spelt, WindowsEnvironment environment)
    {
        var cut = spelt.IndexOf('\\', StringComparison.Ordinal);
        var (root, rest) = cut < 0 ? (spelt, string.Empty) : (spelt[..cut], spelt[(cut + 1)..]);
        var basePath = WindowsRoot(root, environment);
        return basePath.Length == 0 ? string.Empty : PathRules.Windows.Join(basePath, [.. rest.Split('\\', StringSplitOptions.RemoveEmptyEntries)]);
    }

    /// <summary>The folders a walk passes on its way to an agent's own folder — never a name that marks the agent.</summary>
    private static readonly string[] Containers = [".cache", ".config", ".local", "share"];

    /// <summary>The never-list's agent folder names (plan §5, §15q H1): for every catalogue folder, its first segment that is not
    /// a generic container (<c>~/.cache/antigravity</c> → <c>antigravity</c>), lowercased — except Roaming's bare <c>Claude</c>,
    /// which the never-list recognises by its parent instead (a bare <c>claude</c> would match every binary of that name).</summary>
    public static IReadOnlyList<string> NeverListNames =>
        [.. Agents.SelectMany(a => a.Linux.Concat(a.Windows)).Select(MarkingName).Where(n => n.Length > 0 && n != "claude").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static string MarkingName(string spelt) =>
        spelt.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(s => s.ToLowerInvariant()).FirstOrDefault(s => !Containers.Contains(s)) ?? string.Empty;

    /// <summary>Every Linux data folder of the catalogue under <paramref name="home"/>.</summary>
    public static IReadOnlyList<string> LinuxFolders(string home) => [.. Agents.SelectMany(a => a.Linux).Select(f => LinuxFolder(f, home))];

    /// <summary>Every Windows data folder of the catalogue.</summary>
    public static IReadOnlyList<string> WindowsFolders(WindowsEnvironment environment) =>
        [.. Agents.SelectMany(a => a.Windows).Select(f => WindowsFolder(f, environment)).Where(f => f.Length > 0)];

    private static string WindowsRoot(string root, WindowsEnvironment environment) => root switch
    {
        "%USERPROFILE%" => environment.UserProfile,
        "%LOCALAPPDATA%" => environment.LocalAppData,
        "%APPDATA%" => environment.AppData,
        _ => string.Empty,
    };

    private static IReadOnlyList<AgentEntry> Load()
    {
        using var stream = typeof(AgentCatalogue).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"embedded resource {Resource} is missing from the build");
        var file = JsonSerializer.Deserialize(stream, WslCareJsonContext.Default.AgentCatalogueFile)
            ?? throw new InvalidOperationException($"{Resource} is empty");
        return [.. (file.Agents ?? []).Select(Normalised)];
    }

    /// <summary>A member the file omitted binds to <c>null</c> under the source generator (C# doctrine §4a): every list
    /// becomes empty and every text empty here, once, so no reader meets a null.</summary>
    private static AgentEntry Normalised(AgentEntry entry) => entry with
    {
        Binaries = entry.Binaries ?? [],
        NpmPackages = entry.NpmPackages ?? [],
        Linux = entry.Linux ?? [],
        Windows = entry.Windows ?? [],
        NeverEnter = entry.NeverEnter ?? [],
        NeverEnterPrefixes = entry.NeverEnterPrefixes ?? [],
        VersionInLinkTarget = entry.VersionInLinkTarget ?? string.Empty,
        Sessions = entry.Sessions is { } layout ? layout with { Companions = layout.Companions ?? [] } : null,
    };
}
