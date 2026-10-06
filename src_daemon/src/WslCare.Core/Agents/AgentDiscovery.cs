using WslCare.Core.Config;
using System.Text.Json;
using System.Text.RegularExpressions;

using WslCare.Core.Actions;
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

/// <summary>A version read from disk and where it came from — or "not asked" and why.</summary>
public sealed record VersionFound(string Version, string Source)
{
    public static VersionFound NotAsked(string why) => new(string.Empty, why);
}

/// <summary>
/// Which catalogue agents are here (plan §4.6, §15q D3): an agent is TRACKED when one of its binaries is in the folders the
/// invoking user starts programs from, an npm global package of it is installed, or one of its data folders exists. Nothing
/// is ever executed — a binary is looked up, never started; its version is read from disk along ITS OWN link chain (a native
/// install's <c>…/versions/&lt;v&gt;</c>, or the npm package the chain leads into), else "not asked". As ROOT only the folders
/// are looked at: root neither searches the user's folders nor reads their packages.
/// </summary>
/// <remarks>Review R1: a process <c>wsl.exe --exec</c> starts has a PATH without <c>~/.local/bin</c> or nvm's bin and with 30+
/// Windows folders on drvfs (measured 2026-10-05, <c>research/2026-10-03_wsl_exe_facts.md</c>). So on the distro the binaries
/// are looked for in a FIXED list — the user's <c>~/.local/bin</c>, <c>~/.cargo/bin</c>, <c>~/.npm-global/bin</c>, nvm's
/// default, then <c>/usr/local/bin</c>, <c>/usr/bin</c> (the list root uses for the target user) — and in the PATH entries
/// that are neither under the automount root nor on another filesystem than the distro's root, the whole lookup bounded.</remarks>
public static class AgentDiscovery
{
    /// <summary>The most of a <c>package.json</c> read for its version.</summary>
    public static int MaxPackageJsonBytes => Tuning.Current.Int(ConfigKeys.Agents.MaxPackageJsonBytes);

    /// <summary>The most links followed from a binary to what it really is (the kernel's own ELOOP limit).</summary>
    public const int MaxLinkHops = 40;

    /// <summary>The whole binary lookup of one discovery (review R1: a PATH entry on a stopped 9p share must not hold the answer).</summary>
    public static TimeSpan LookupCeiling => Tuning.Current.Seconds(ConfigKeys.Agents.LookupCeilingSeconds);

    public const string NotAskedAsRoot = "not asked as root: root neither looks up the user's binaries nor reads their packages";

    public const string Binary = "binary";
    public const string Npm = "npm";
    public const string Folder = "folder";

    /// <summary>Every catalogue agent as this side sees it. <paramref name="pathVariable"/> is the <c>PATH</c> this process got
    /// (its usable entries are searched after the fixed folders); <paramref name="asRoot"/> limits discovery to folders.</summary>
    public static IReadOnlyList<AgentPresence> Discover(IHostPaths paths, IFileSystem files, string? pathVariable, bool asRoot)
    {
        var side = DiscoverySide.Of(paths);
        var search = asRoot ? [] : side.BinaryFolders(files, pathVariable);
        return [.. AgentCatalogue.Agents.Select(entry => Discover(entry, side, files, search, asRoot))];
    }

    private static AgentPresence Discover(AgentEntry entry, DiscoverySide side, IFileSystem files, IReadOnlyList<string> search, bool asRoot)
    {
        var folders = side.Folders(entry);
        var binaries = asRoot ? [] : Bounded.Run(() => entry.Binaries.SelectMany(b => Lookup(b, search, side.Windows)).ToList(), LookupCeiling, []);
        var package = asRoot ? string.Empty : side.NpmRoots(files).Select(root => entry.NpmPackages.Select(p => Path.Combine(root, p)).FirstOrDefault(files.DirectoryExists)).FirstOrDefault(p => p is not null) ?? string.Empty;
        IReadOnlyList<string> detected = [.. Detected(binaries.Count > 0, package.Length > 0, folders.Any(files.DirectoryExists))];
        var version = asRoot ? VersionFound.NotAsked(NotAskedAsRoot) : VersionOf(entry, binaries, package, files);
        return new AgentPresence(entry, detected, binaries, version.Version, version.Source, folders, side.SessionsUnder(entry));
    }

    private static IEnumerable<string> Detected(bool binary, bool npm, bool folder) =>
        new[] { (binary, Binary), (npm, Npm), (folder, Folder) }.Where(d => d.Item1).Select(d => d.Item2);

    private static IEnumerable<FoundBinary> Lookup(string name, IReadOnlyList<string> folders, bool windows) =>
        ExecutableResolver.ResolveIn(name, folders, windows) is ResolvedExecutable.Found found ? [new FoundBinary(name, found.Path)] : [];

    /// <summary>The version of the binary that was FOUND (review R3): along its link chain, a native install's version in a link
    /// target, or the npm package the chain leads into; only when no binary was found, an installed npm package's — named so.</summary>
    private static VersionFound VersionOf(AgentEntry entry, IReadOnlyList<FoundBinary> binaries, string package, IFileSystem files) =>
        binaries.Count > 0 ? FromChain(entry, binaries[0], files)
        : FromPackage(package, files) is { Length: > 0 } packaged ? new VersionFound(packaged, $"{package}/package.json (no binary was found; an installed npm package)")
        : VersionFound.NotAsked("not asked: no binary or npm package names a version on disk (nothing is executed to ask)");

    private static VersionFound FromChain(AgentEntry entry, FoundBinary binary, IFileSystem files)
    {
        var chain = Chain(binary.Path, files);
        var fromLink = chain.Select(hop => FromLinkTarget(entry, hop)).FirstOrDefault(v => v.Length > 0);
        if (fromLink is { } linked)
        {
            return new VersionFound(linked, $"the link target of {binary.Path}");
        }

        var root = chain.Select(hop => PackageRoot(entry, hop)).FirstOrDefault(r => r.Length > 0) ?? string.Empty;
        return FromPackage(root, files) is { Length: > 0 } packaged
            ? new VersionFound(packaged, $"{root}/package.json (the package {binary.Path} runs)")
            : VersionFound.NotAsked($"not asked: neither {binary.Path}'s links nor its package name a version (nothing is executed to ask)");
    }

    /// <summary>The binary and every path its links lead to, in order, at most <see cref="MaxLinkHops"/> hops.</summary>
    public static IReadOnlyList<string> Chain(string path, IFileSystem files)
    {
        var chain = new List<string> { path };
        while (chain.Count <= MaxLinkHops && files.ReadLink(chain[^1]) is LinkReadResult.Target target)
        {
            chain.Add(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(chain[^1]) ?? string.Empty, target.Path)));
        }

        return chain;
    }

    private static string FromLinkTarget(AgentEntry entry, string hop) =>
        entry.VersionInLinkTarget.Length > 0 && Regex.Match(hop.Replace('\\', '/'), entry.VersionInLinkTarget, RegexOptions.CultureInvariant, Tuning.Current.Milliseconds(ConfigKeys.Patterns.MatchTimeoutMilliseconds)) is { Success: true } m
            ? m.Groups["v"].Value
            : string.Empty;

    /// <summary>The npm package folder a path lies in (<c>…/lib/node_modules/&lt;package&gt;/…</c>), for one of the entry's packages.</summary>
    private static string PackageRoot(AgentEntry entry, string hop)
    {
        var path = hop.Replace('\\', '/');
        return entry.NpmPackages.Select(p => $"/node_modules/{p}/").Select(marker => (Marker: marker, At: path.IndexOf(marker, StringComparison.Ordinal)))
            .Where(x => x.At >= 0).Select(x => hop[..(x.At + x.Marker.Length - 1)]).FirstOrDefault() ?? string.Empty;
    }

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

    /// <summary>The side discovery looks at — a closed choice (review R13).</summary>
    private abstract record DiscoverySide
    {
        public abstract bool Windows { get; }

        public static DiscoverySide Of(IHostPaths paths) => paths switch
        {
            LinuxHostPaths linux => new Distro(linux),
            WindowsHostPaths windows => new WindowsHost(windows),
            _ => throw new PlatformNotSupportedException($"no agent folders for a {paths.GetType().Name} layout"),
        };

        public abstract IReadOnlyList<string> Folders(AgentEntry entry);

        public abstract string SessionsUnder(AgentEntry entry);

        public abstract IReadOnlyList<string> NpmRoots(IFileSystem files);

        public abstract IReadOnlyList<string> BinaryFolders(IFileSystem files, string? pathVariable);
    }

    /// <summary>The distro: the catalogue's <c>~/…</c> under the home the paths follow; the fixed bin folders, then the usable PATH.</summary>
    private sealed record Distro(LinuxHostPaths Paths) : DiscoverySide
    {
        public override bool Windows => false;

        public override IReadOnlyList<string> Folders(AgentEntry entry) => [.. entry.Linux.Select(f => AgentCatalogue.LinuxFolder(f, Paths.Home))];

        public override string SessionsUnder(AgentEntry entry) =>
            entry.Sessions is { LinuxUnder.Length: > 0 } layout ? AgentCatalogue.LinuxFolder(layout.LinuxUnder, Paths.Home) : string.Empty;

        /// <summary>The npm global <c>node_modules</c> folders: every installed nvm version's, the usual prefixes, the system ones.</summary>
        public override IReadOnlyList<string> NpmRoots(IFileSystem files)
        {
            var rules = Paths.Rules;
            var nvm = files.ListEntries(rules.Join(Paths.Home, ".nvm", "versions", "node"))
                .Where(e => e.Kind == EntryKind.Directory)
                .Select(e => rules.Join(Paths.Home, ".nvm", "versions", "node", e.Name, "lib", "node_modules"));
            return [.. nvm,
                rules.Join(Paths.Home, ".npm-global", "lib", "node_modules"),
                rules.Join(Paths.Home, ".local", "lib", "node_modules"),
                Paths.DistroPath("/usr/local/lib/node_modules"),
                Paths.DistroPath("/usr/lib/node_modules")];
        }

        public override IReadOnlyList<string> BinaryFolders(IFileSystem files, string? pathVariable)
        {
            var user = new TargetUser(Environment.UserName, (int)RegularFiles.EffectiveUid(), Paths.ToDistro(Paths.Home));
            var fixedFolders = TargetUserCommands.BinFolders(user, Paths, files).Select(f => f.OnDisk);
            return [.. fixedFolders.Concat(UsablePath(files, pathVariable)).Distinct(StringComparer.Ordinal)];
        }

        /// <summary>The PATH entries that are not under the automount root (Windows' folders on drvfs) and lie on the distro root's
        /// filesystem — a Windows npm shim is not the distro's binary, and a stat over 9p is not waited on.</summary>
        private IEnumerable<string> UsablePath(IFileSystem files, string? pathVariable)
        {
            var automount = Paths.DistroPath(Health.WindowsProfiles.AutomountRoot(Paths, files));
            var rootDevice = files.DeviceOf(Paths.DistroPath("/"));
            return (pathVariable ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Where(e => Path.IsPathFullyQualified(e) && !IsUnder(e, automount))
                .Where(e => files.DeviceOf(e) is { } device && device == rootDevice);
        }

        private static bool IsUnder(string path, string root) =>
            PathRules.ForThisOs.IsSameOrUnder(Path.GetFullPath(path), Path.GetFullPath(root));
    }

    /// <summary>The Windows host: the catalogue's <c>%ROOT%\…</c> under the Windows folders; the PATH as Windows has it.</summary>
    private sealed record WindowsHost(WindowsHostPaths Paths) : DiscoverySide
    {
        public override bool Windows => true;

        public override IReadOnlyList<string> Folders(AgentEntry entry) =>
            [.. entry.Windows.Select(f => AgentCatalogue.WindowsFolder(f, Paths.Folders)).Where(f => f.Length > 0)];

        public override string SessionsUnder(AgentEntry entry) =>
            entry.Sessions is { WindowsUnder.Length: > 0 } layout ? AgentCatalogue.WindowsFolder(layout.WindowsUnder, Paths.Folders) : string.Empty;

        public override IReadOnlyList<string> NpmRoots(IFileSystem files) => [PathRules.Windows.Join(Paths.Folders.AppData, "npm", "node_modules")];

        public override IReadOnlyList<string> BinaryFolders(IFileSystem files, string? pathVariable) =>
            [.. (pathVariable ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim().Trim('"')).Where(e => e.Length > 0 && Path.IsPathFullyQualified(e))];
    }
}
