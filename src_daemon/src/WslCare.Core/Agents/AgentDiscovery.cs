using System.Text.Json;
using System.Text.RegularExpressions;

using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;

namespace WslCare.Core.Agents;

/// <summary>A binary of the agent found on <c>PATH</c> — found, never started (plan §15q D3).</summary>
public sealed record FoundBinary(string Name, string Path);

/// <summary>What discovery found of one agent (plan §4.6): how it was detected, its binaries and version (unprivileged only),
/// its data folders as paths on this side, and where its session layout starts.</summary>
/// <param name="DetectedBy"><c>binary</c>, <c>npm</c> and / or <c>folder</c>; empty = not tracked.</param>
/// <param name="Version">The version read from disk, or empty with <paramref name="VersionReason"/>.</param>
public sealed record AgentPresence(
    AgentEntry Entry,
    IReadOnlyList<string> DetectedBy,
    IReadOnlyList<FoundBinary> Binaries,
    string Version,
    string VersionReason,
    IReadOnlyList<string> Folders,
    string SessionsUnder)
{
    public bool Tracked => DetectedBy.Count > 0;

    /// <summary>Why a manual agent may not be walked (plan §15q R2.1); empty for every catalogue agent and an accepted one.</summary>
    public string Refusal { get; init; } = string.Empty;

    public AgentTarget Target => new(Entry, Folders, SessionsUnder, Refusal);
}

/// <summary>
/// Which catalogue agents are here (plan §4.6, §15q D3): an agent is TRACKED when one of its binaries is on <c>PATH</c>, an npm
/// global package of it is installed, or one of its data folders exists. Nothing is ever executed — a binary is looked up,
/// never started; a version is read from disk (the binary's link target, or the npm package's <c>package.json</c>) or is
/// "not asked". As ROOT only the folders are looked at: root neither searches the user's <c>PATH</c> nor reads their packages.
/// </summary>
public static class AgentDiscovery
{
    /// <summary>The most of a <c>package.json</c> read for its version.</summary>
    public const int MaxPackageJsonBytes = 1024 * 1024;

    public const string NotAskedAsRoot = "not asked as root: root neither looks up the user's PATH nor reads their packages";

    public const string Binary = "binary";
    public const string Npm = "npm";
    public const string Folder = "folder";

    /// <summary>Every catalogue agent as this side sees it. <paramref name="pathVariable"/> is the <c>PATH</c> binaries are
    /// looked up on (this process's own; a test passes its own); <paramref name="asRoot"/> limits discovery to folders.</summary>
    public static IReadOnlyList<AgentPresence> Discover(IHostPaths paths, IFileSystem files, string? pathVariable, bool asRoot) =>
        [.. AgentCatalogue.Agents.Select(entry => Discover(entry, Side.Of(paths), files, pathVariable, asRoot))];

    private static AgentPresence Discover(AgentEntry entry, Side side, IFileSystem files, string? pathVariable, bool asRoot)
    {
        var folders = side.Folders(entry);
        var binaries = asRoot ? [] : entry.Binaries.SelectMany(b => Lookup(b, pathVariable, side.Windows)).ToList();
        var package = asRoot ? string.Empty : side.NpmRoots(files).Select(root => entry.NpmPackages.Select(p => Path.Combine(root, p)).FirstOrDefault(files.DirectoryExists)).FirstOrDefault(p => p is not null) ?? string.Empty;
        IReadOnlyList<string> detected =
        [
            .. binaries.Count > 0 ? [Binary] : Array.Empty<string>(),
            .. package.Length > 0 ? [Npm] : Array.Empty<string>(),
            .. folders.Any(files.DirectoryExists) ? [Folder] : Array.Empty<string>(),
        ];
        var (version, why) = asRoot ? (string.Empty, NotAskedAsRoot) : VersionOf(entry, binaries, package, files);
        return new AgentPresence(entry, detected, binaries, version, why, folders, side.SessionsUnder(entry));
    }

    private static IEnumerable<FoundBinary> Lookup(string name, string? pathVariable, bool windows) =>
        ExecutableResolver.Resolve(name, pathVariable, windows) is ResolvedExecutable.Found found ? [new FoundBinary(name, found.Path)] : [];

    /// <summary>The version from disk: the first binary's link target (a native install's <c>…/versions/&lt;v&gt;</c>), else the
    /// npm package's <c>package.json</c>; nothing is started.</summary>
    private static (string Version, string Reason) VersionOf(AgentEntry entry, IReadOnlyList<FoundBinary> binaries, string package, IFileSystem files) =>
        FromLink(entry, binaries, files) is { Length: > 0 } linked ? (linked, string.Empty)
        : FromPackage(package, files) is { Length: > 0 } packaged ? (packaged, string.Empty)
        : (string.Empty, "not asked: no link target names a version and no npm package.json was found (nothing is executed to ask)");

    private static string FromLink(AgentEntry entry, IReadOnlyList<FoundBinary> binaries, IFileSystem files) =>
        entry.VersionInLinkTarget.Length == 0 || binaries.Count == 0 || files.ReadLink(binaries[0].Path) is not LinkReadResult.Target target
            ? string.Empty
            : Regex.Match(target.Path, entry.VersionInLinkTarget, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)) is { Success: true } m ? m.Groups["v"].Value : string.Empty;

    private static string FromPackage(string package, IFileSystem files)
    {
        if (package.Length == 0 || files.ReadRegularFile(Path.Combine(package, "package.json"), MaxPackageJsonBytes) is not FileReadResult.Content content)
        {
            return string.Empty;
        }

        try
        {
            using var json = JsonDocument.Parse(content.Bytes);
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>The side's folder spellings resolved: the catalogue's <c>~/…</c> under the home the paths follow (the TARGET
    /// user's as root), or its <c>%ROOT%\…</c> under the Windows folders.</summary>
    private sealed record Side(LinuxHostPaths? Linux, WindowsHostPaths? Windows_)
    {
        public bool Windows => Windows_ is not null;

        public static Side Of(IHostPaths paths) => paths switch
        {
            LinuxHostPaths linux => new(linux, null),
            WindowsHostPaths windows => new(null, windows),
            _ => throw new PlatformNotSupportedException($"no agent folders for a {paths.GetType().Name} layout"),
        };

        public IReadOnlyList<string> Folders(AgentEntry entry) =>
            Linux is { } linux
                ? [.. entry.Linux.Select(f => AgentCatalogue.LinuxFolder(f, linux.Home))]
                : [.. entry.Windows.Select(f => AgentCatalogue.WindowsFolder(f, Windows_!.Folders)).Where(f => f.Length > 0)];

        public string SessionsUnder(AgentEntry entry) => entry.Sessions switch
        {
            null => string.Empty,
            { } layout when Linux is { } linux => layout.LinuxUnder.Length == 0 ? string.Empty : AgentCatalogue.LinuxFolder(layout.LinuxUnder, linux.Home),
            { } layout => layout.WindowsUnder.Length == 0 ? string.Empty : AgentCatalogue.WindowsFolder(layout.WindowsUnder, Windows_!.Folders),
        };

        /// <summary>The npm global <c>node_modules</c> folders of this side that exist: every installed nvm version's, the
        /// usual prefixes, the system ones (Linux); <c>%APPDATA%\npm\node_modules</c> (Windows).</summary>
        public IReadOnlyList<string> NpmRoots(IFileSystem files) => Linux is { } linux ? LinuxNpmRoots(linux, files) : [PathRules.Windows.Join(Windows_!.Folders.AppData, "npm", "node_modules")];

        private static IReadOnlyList<string> LinuxNpmRoots(LinuxHostPaths linux, IFileSystem files)
        {
            var rules = linux.Rules;
            var nvm = files.ListEntries(rules.Join(linux.Home, ".nvm", "versions", "node"))
                .Where(e => e.Kind == EntryKind.Directory)
                .Select(e => rules.Join(linux.Home, ".nvm", "versions", "node", e.Name, "lib", "node_modules"));
            return [.. nvm,
                rules.Join(linux.Home, ".npm-global", "lib", "node_modules"),
                rules.Join(linux.Home, ".local", "lib", "node_modules"),
                linux.DistroPath("/usr/local/lib/node_modules"),
                linux.DistroPath("/usr/lib/node_modules")];
        }
    }
}
