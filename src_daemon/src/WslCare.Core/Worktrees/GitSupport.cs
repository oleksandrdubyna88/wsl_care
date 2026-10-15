using WslCare.Core.Config;
using WslCare.Core.Processes;

namespace WslCare.Core.Worktrees;

/// <summary>The bounds of one git question, from the configuration in force.</summary>
/// <param name="Timeout">How long one call may run before its tree is killed (<c>worktrees.gitTimeoutSeconds</c>).</param>
/// <param name="NamesPerCall">The most names one call carries (<c>worktrees.gitNamesPerCall</c>).</param>
/// <param name="CommandLineMaxChars">The most characters of encoded command line one call may have (<c>worktrees.gitCommandLineMaxChars</c>).</param>
public sealed record GitLimits(TimeSpan Timeout, int NamesPerCall, int CommandLineMaxChars)
{
    public static GitLimits From(EffectiveConfig config) => new(
        TimeSpan.FromSeconds(config.Int(ConfigKeys.WorktreeGit.TimeoutSeconds)),
        config.Int(ConfigKeys.WorktreeGit.NamesPerCall),
        config.Int(ConfigKeys.WorktreeGit.CommandLineMaxChars));
}

/// <summary>
/// The environment git starts with: CLEAN plus what git needs, and NO user or system git configuration (PLAN_disk_hygiene_windows_E19.md
/// §3 decision 5, measured 2026-10-15 against git 2.55.0.windows.3). With a home set, a user's <c>core.excludesFile</c> or
/// <c>~/.config/git/ignore</c> would make <c>check-ignore</c> answer with the USER's rules as if they were the repository's;
/// <c>GIT_CONFIG_GLOBAL=NUL</c> alone does not stop the XDG file — only an environment without <c>HOME</c>, <c>USERPROFILE</c> and
/// <c>XDG_CONFIG_HOME</c> does. Nothing is copied from the parent but the four names git needs to start.
/// </summary>
public static class GitEnvironment
{
    private static readonly string[] CopiedFromParent = ["SystemRoot", "WINDIR", "TEMP", "TMP"];

    /// <summary>The variables for a git run whose discovery may not climb to <paramref name="ceiling"/> or above.</summary>
    public static CommandEnvironment.Clean For(Func<string, string> parent, string ceiling)
    {
        var variables = CopiedFromParent
            .Select(name => KeyValuePair.Create(name, parent(name)))
            .Where(pair => pair.Value.Length > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        variables["GIT_OPTIONAL_LOCKS"] = "0";
        variables["GIT_TERMINAL_PROMPT"] = "0";
        variables["GIT_CONFIG_NOSYSTEM"] = "1";
        variables["GIT_CONFIG_GLOBAL"] = "NUL";
        variables["GIT_CEILING_DIRECTORIES"] = ceiling;
        return new CommandEnvironment.Clean(variables);
    }

    /// <summary>This process's variables, by name; empty for one that is not set.</summary>
    public static string ThisProcess(string name) => Environment.GetEnvironmentVariable(name) ?? string.Empty;
}

/// <summary>Names split into calls bounded by BOTH a count and the encoded command line (PLAN_disk_hygiene_windows_E19.md §3 decision 4).</summary>
public static class GitBatches
{
    /// <summary>The calls <paramref name="names"/> ride in, in order, and the names that cannot fit a call even alone.</summary>
    /// <param name="lengthOf">The encoded length of the longest command line a call carrying exactly these names would have.</param>
    public static (IReadOnlyList<IReadOnlyList<string>> Calls, IReadOnlyList<string> TooLong) Split(
        IReadOnlyList<string> names, int maxCount, int maxChars, Func<IReadOnlyList<string>, int> lengthOf)
    {
        var calls = new List<IReadOnlyList<string>>();
        var tooLong = new List<string>();
        var current = new List<string>();
        foreach (var name in names)
        {
            if (lengthOf([name]) > maxChars)
            {
                tooLong.Add(name);
                continue;
            }

            if (current.Count > 0 && (current.Count >= maxCount || lengthOf([.. current, name]) > maxChars))
            {
                calls.Add([.. current]);
                current.Clear();
            }

            current.Add(name);
        }

        if (current.Count > 0)
        {
            calls.Add([.. current]);
        }

        return (calls, tooLong);
    }

    /// <summary>The encoded command line of <paramref name="argv"/> (program first), as Windows will be handed it.</summary>
    public static int EncodedLength(IReadOnlyList<string> argv) => WindowsCommandLine.Of(argv[0], [.. argv.Skip(1)]).Length;
}

/// <summary>Whether a folder is at or beneath a root — by SEGMENTS, case-insensitively, over Windows spellings (<c>/</c> and <c>\</c> alike);
/// a path with an empty, <c>.</c> or <c>..</c> segment, or that is not a drive path, is beneath nothing.</summary>
public static class FolderBeneath
{
    private static readonly char[] Separators = ['/', '\\'];

    public static bool AtOrUnder(string root, string folder) =>
        Segments(root) is { Count: > 0 } rootSegments
        && Segments(folder) is { Count: > 0 } folderSegments
        && folderSegments.Count >= rootSegments.Count
        && rootSegments.Zip(folderSegments).All(pair => string.Equals(pair.First, pair.Second, StringComparison.OrdinalIgnoreCase));

    /// <summary>The drive and the segments after it; empty when the path is not an absolute drive path or holds a bad segment.</summary>
    private static List<string> Segments(string path)
    {
        var isDrive = path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && Separators.Contains(path[2]);
        var parts = path.Split(Separators).Select((part, i) => i == 0 ? part.ToUpperInvariant() : part).ToList();
        var inner = parts.Skip(1).Where((part, i) => !(part.Length == 0 && i == parts.Count - 2)).ToList();
        return isDrive && inner.All(part => part.Length > 0 && part is not ("." or "..")) ? [parts[0], .. inner] : [];
    }
}
