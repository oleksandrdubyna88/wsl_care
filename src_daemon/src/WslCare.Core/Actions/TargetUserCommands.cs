using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;

namespace WslCare.Core.Actions;

/// <summary>One bin folder of the target user: where the distro sees it (the child's <c>PATH</c>) and where THIS process
/// reads it (the same on the machine; under the sandbox root in a test).</summary>
public sealed record UserBinFolder(string DistroPath, string OnDisk);

/// <summary>What building a user-scoped command produced: the request, or why not.</summary>
public abstract record UserCommand
{
    private UserCommand()
    {
    }

    public sealed record Ready(CommandRequest Request) : UserCommand;

    public sealed record Refused(string Reason) : UserCommand;
}

/// <summary>
/// The <c>runuser</c> wrapper (plan §15c #2): a user-scoped tool runs as the target user through
/// <c>runuser -u &lt;user&gt; -- &lt;full path&gt; &lt;args…&gt;</c> (<see cref="TargetUserArgv"/>), its file resolved BEFORE
/// the start against a FIXED list of that user's bin folders — <c>~/.nvm/versions/node/&lt;default&gt;/bin</c>,
/// <c>~/.local/bin</c>, <c>~/.cargo/bin</c>, <c>/usr/local/bin</c>, <c>/usr/bin</c> — and a clean environment holding
/// only <c>HOME</c>, <c>USER</c>, <c>LOGNAME</c> and a <c>PATH</c> of those folders. Never <c>bash -ic</c> (plan §15c #3).
/// </summary>
public static class TargetUserCommands
{
    /// <summary>The bin folders in lookup order. nvm's default version is read from <c>~/.nvm/alias/default</c>; one that
    /// names no installed version (an alias such as <c>lts/*</c>) leaves that folder out rather than guessing.</summary>
    public static IReadOnlyList<UserBinFolder> BinFolders(TargetUser user, LinuxHostPaths paths, IFileSystem files)
    {
        var rules = paths.Rules;
        IReadOnlyList<string> nvm = NvmDefaultBin(user, paths, files) is { } bin ? [bin] : [];
        IReadOnlyList<string> distro = [.. nvm, .. HomeBins.Select(segments => rules.Join(user.Home, segments)), .. SystemBins];
        return [.. distro.Select(d => new UserBinFolder(d, paths.DistroPath(d)))];
    }

    /// <summary><paramref name="arguments"/> bound into <paramref name="template"/> and wrapped for <paramref name="user"/>.</summary>
    public static UserCommand Build(CommandTemplate template, IReadOnlyList<string> arguments, TargetUser user, IReadOnlyList<UserBinFolder> folders)
    {
        if (template.Scope != CommandScope.User)
        {
            return new UserCommand.Refused($"{template.Name} is not a user-scoped template");
        }

        return ExecutableResolver.ResolveIn(template.Executable, [.. folders.Select(f => f.OnDisk)], OperatingSystem.IsWindows()) switch
        {
            ResolvedExecutable.Found found => new UserCommand.Ready(new CommandRequest(TargetUserArgv.Build(user.Name, found.Path, arguments), template.Ceiling)
            {
                OutputCapChars = template.OutputCapChars,
                Environment = new CommandEnvironment.Clean(Environment(user, folders)),
            }),
            ResolvedExecutable.NotFound missing => new UserCommand.Refused($"{template.Executable} is not in {user.Name}'s bin folders: {missing.Reason}"),
            _ => throw new System.Diagnostics.UnreachableException("ResolvedExecutable is a closed set"),
        };
    }

    /// <summary>The whole environment the child gets: nothing of this process's.</summary>
    public static IReadOnlyDictionary<string, string> Environment(TargetUser user, IReadOnlyList<UserBinFolder> folders) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = user.Home,
            ["USER"] = user.Name,
            ["LOGNAME"] = user.Name,
            ["PATH"] = string.Join(':', folders.Select(f => f.DistroPath)),
        };

    /// <summary>The user's own bin folders, after nvm's default (E7.S1/S2 review R1: the same list root uses for the target user and
    /// <c>agents list</c> uses for the invoking user — a process <c>wsl.exe --exec</c> starts has none of them on its PATH).</summary>
    private static readonly string[][] HomeBins = [[".local", "bin"], [".cargo", "bin"], [".npm-global", "bin"]];

    /// <summary>The system's bin folders, last.</summary>
    private static readonly string[] SystemBins = ["/usr/local/bin", "/usr/bin"];

    /// <summary>nvm's default alias is one short line.</summary>
    private const int MaxAliasBytes = 4096;

    /// <summary><c>~/.nvm/versions/node/&lt;the default version&gt;/bin</c> (distro path), or <c>null</c>.</summary>
    private static string? NvmDefaultBin(TargetUser user, LinuxHostPaths paths, IFileSystem files)
    {
        var rules = paths.Rules;
        var versionsDir = rules.Join(user.Home, ".nvm", "versions", "node");
        // The target user's own file, read by root: owner-checked, never through a link, never waited on (plan §15q R1.1).
        var alias = files.ReadUserFile(paths.DistroPath(rules.Join(user.Home, ".nvm", "alias", "default")), MaxAliasBytes, RegularFiles.HomeFileOwner(user.Uid), paths.DistroPath(user.Home));
        if (alias is not FileReadResult.Content content)
        {
            return null;
        }

        var installed = files.ListDirectories(paths.DistroPath(versionsDir)).Select(d => Path.GetFileName(d.TrimEnd('/', '\\'))).ToList();
        return NvmVersion.Choose(System.Text.Encoding.UTF8.GetString(content.Bytes).Trim(), installed) is { } chosen ? rules.Join(versionsDir, chosen, "bin") : null;
    }
}

/// <summary>nvm's alias resolution, the part the daemon needs: an exact installed version, a version prefix (<c>22</c>,
/// <c>v22.11</c>) → the highest installed match, <c>node</c> / <c>stable</c> → the highest installed; anything else
/// (<c>lts/*</c>, a named alias) → none.</summary>
public static class NvmVersion
{
    public static string? Choose(string alias, IReadOnlyList<string> installed)
    {
        var versions = installed.Select(v => (Name: v, Parts: Parse(v))).Where(v => v.Parts is not null).OrderByDescending(v => v.Parts!, Comparer).ToList();
        if (alias is "node" or "stable")
        {
            return versions.FirstOrDefault().Name;
        }

        var wanted = Parse(alias.StartsWith('v') ? alias : "v" + alias);
        return wanted is null ? null : HighestMatching(versions, wanted);
    }

    /// <summary>The highest installed version that starts with <paramref name="wanted"/>'s numbers.</summary>
    private static string? HighestMatching(IReadOnlyList<(string Name, IReadOnlyList<int>? Parts)> versions, IReadOnlyList<int> wanted) =>
        versions.FirstOrDefault(v => v.Parts!.Take(wanted.Count).SequenceEqual(wanted) && v.Parts!.Count >= wanted.Count).Name;

    private static readonly Comparer<IReadOnlyList<int>> Comparer = Comparer<IReadOnlyList<int>>.Create((a, b) =>
        a.Zip(b).Select(p => p.First.CompareTo(p.Second)).FirstOrDefault(c => c != 0, a.Count.CompareTo(b.Count)));

    /// <summary><c>v22.11.0</c> → [22, 11, 0]; <c>null</c> for anything that is not <c>v</c> and dot-separated numbers.</summary>
    private static IReadOnlyList<int>? Parse(string text)
    {
        if (text.Length < 2 || text[0] != 'v')
        {
            return null;
        }

        return Numbers(text[1..].Split('.'));
    }

    /// <summary>One to three dot-separated numbers of at most six digits; <c>null</c> otherwise.</summary>
    private static IReadOnlyList<int>? Numbers(string[] parts) =>
        parts.Length <= 3 && parts.All(IsVersionPart) ? [.. parts.Select(p => int.Parse(p, System.Globalization.CultureInfo.InvariantCulture))] : null;

    private static bool IsVersionPart(string part) => part.Length is > 0 and <= 6 && part.All(char.IsAsciiDigit);
}
