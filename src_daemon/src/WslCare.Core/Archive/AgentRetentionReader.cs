using System.Text.Json;

using WslCare.Core.Agents;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Archive;

/// <summary>An agent's own retention as this side found it (plan §15r D10) — a closed answer (E9.S1 review round m8): known, a
/// number of days, or unknown and why; never a null that could mean either.</summary>
public abstract record RetentionFound
{
    private RetentionFound()
    {
    }

    /// <summary>Where it came from: a file, the catalogue's documented default, or what was checked.</summary>
    public abstract string From { get; }

    /// <summary>What a person must know: a retention of 0 (the agent keeps nothing), an unreadable value, an unknown retention.</summary>
    public abstract IReadOnlyList<string> Warnings { get; }

    /// <summary>The agent deletes a session <paramref name="Days"/> days after its last use.</summary>
    public sealed record Known(int Days, string Source, IReadOnlyList<string> Notes) : RetentionFound
    {
        public override string From => Source;

        public override IReadOnlyList<string> Warnings => Notes;
    }

    /// <summary>Whether and when the agent deletes its own sessions is not known — <paramref name="Why"/> says what was checked.</summary>
    public sealed record Unknown(string Why, IReadOnlyList<string> Notes) : RetentionFound
    {
        public override string From => Why;

        public override IReadOnlyList<string> Warnings => Notes;
    }
}

/// <summary>
/// Plan §15r D10, review M7: reads where an agent keeps its own deletion, as the USER, through the bounded reader — that one key
/// only. For Claude Code (<see cref="RetentionSources.ClaudeSettings"/>): the managed settings first (an administrator's value
/// wins), then <c>settings.json</c> under <c>CLAUDE_CONFIG_DIR</c> when the process's environment names it, else under
/// <c>~/.claude</c>; absent everywhere → the catalogue's documented default. Project-level settings are a stated residual
/// (not read). A value of 0 is a loud warning.
/// </summary>
public static class AgentRetentionReader
{
    public const string ClaudeKey = "cleanupPeriodDays";

    /// <summary>The variable that moves Claude Code's configuration (and its sessions) out of <c>~/.claude</c>.</summary>
    public const string ClaudeConfigDir = "CLAUDE_CONFIG_DIR";

    /// <summary>The managed settings of Claude Code inside the distribution (Anthropic's documented place).</summary>
    public const string LinuxManaged = "/etc/claude-code/managed-settings.json";

    /// <summary>Where Claude Code's managed settings live on Windows, under <c>Program Files</c>.</summary>
    public const string WindowsManagedUnderProgramFiles = @"ClaudeCode\managed-settings.json";

    /// <summary>The files Claude Code's retention is read from on this side, in the order the first one holding the key wins.</summary>
    public static IReadOnlyList<string> ClaudeFiles(IHostPaths paths, Func<string, string?> environment) =>
    [
        paths is LinuxHostPaths linux ? linux.DistroPath(LinuxManaged) : Path.Combine(environment("ProgramFiles") ?? @"C:\Program Files", WindowsManagedUnderProgramFiles),
        Path.Combine(ClaudeConfigFolder(paths, environment), "settings.json"),
    ];

    /// <summary>Claude Code's configuration folder as this process sees it: <c>CLAUDE_CONFIG_DIR</c> when set, else <c>.claude</c>
    /// under the home.</summary>
    public static string ClaudeConfigFolder(IHostPaths paths, Func<string, string?> environment) =>
        (paths, environment(ClaudeConfigDir) ?? string.Empty) switch
        {
            (LinuxHostPaths linux, { } configured) when configured.StartsWith('/') => linux.DistroPath(configured),
            (WindowsHostPaths, { } configured) when PathRules.Windows.IsAbsolute(configured) => configured,
            _ => paths.Rules.Join(paths.Home, ".claude"),
        };

    /// <summary><paramref name="entry"/>'s own retention on this side.</summary>
    public static RetentionFound Read(AgentEntry entry, IHostPaths paths, IFileSystem files, Func<string, string?> environment) =>
        entry.Archive?.Retention is { Source: RetentionSources.ClaudeSettings } retention
            ? Claude(retention, paths, files, environment)
            : Unknown(entry);

    /// <summary>E9.S1 review round m8: an agent whose own deletion is not known is SAID to be — the archive then takes its sessions at
    /// <c>archive.olderThanDays</c>, which no agent rule shortens.</summary>
    private static RetentionFound Unknown(AgentEntry entry)
    {
        var why = "none known: " + (entry.Archive?.Retention.Checked ?? "no archive block");
        var warning = $"{entry.Name}: whether and when it deletes its own sessions is not known ({why}) — the archive takes them at {ConfigKeys.Archive.OlderThanDays.Name}";
        return new RetentionFound.Unknown(why, [warning]);
    }

    private static RetentionFound Claude(AgentRetention retention, IHostPaths paths, IFileSystem files, Func<string, string?> environment)
    {
        var read = ClaudeFiles(paths, environment).Select(path => (Path: path, Value: ValueIn(files, path))).ToList();
        var problems = read.Where(r => r.Value is ReadValue.Unusable).Select(r => $"{r.Path}: {((ReadValue.Unusable)r.Value).Why} — not taken").ToList();
        return read.FirstOrDefault(r => r.Value is ReadValue.Days) is { Path: { } path, Value: ReadValue.Days found }
            ? new RetentionFound.Known(found.Value, path, [.. problems, .. ZeroWarning(found.Value)])
            : new RetentionFound.Known(retention.DefaultDays, $"the documented default ({retention.DefaultDays} days): {ClaudeKey} is set in none of {string.Join(", ", read.Select(r => r.Path))}", problems);
    }

    private static IEnumerable<string> ZeroWarning(int days) =>
        days == 0 ? [$"Claude Code's {ClaudeKey} is 0: it keeps NO session past its own next start — the archive takes them as early as it may (1 day)"] : [];

    /// <summary>What one settings file says.</summary>
    private abstract record ReadValue
    {
        private ReadValue()
        {
        }

        public sealed record Absent : ReadValue;

        public sealed record Days(int Value) : ReadValue;

        public sealed record Unusable(string Why) : ReadValue;
    }

    private static ReadValue ValueIn(IFileSystem files, string path) => files.ReadRegularFile(path, Tuning.Current.Int(ConfigKeys.UserFiles.MaxJsonBytes)) switch
    {
        FileReadResult.Content content => Parse(content.Bytes),
        FileReadResult.Unreadable unreadable when files.FileExists(path) => new ReadValue.Unusable(unreadable.Reason),
        _ => new ReadValue.Absent(),
    };

    private static ReadValue Parse(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(ClaudeKey, out var value)
                ? Days(value)
                : new ReadValue.Absent();
        }
        catch (JsonException e)
        {
            return new ReadValue.Unusable($"not JSON ({e.Message})");
        }
    }

    private static ReadValue Days(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var days) && days >= 0
            ? new ReadValue.Days(days)
            : new ReadValue.Unusable($"{ClaudeKey} is not a whole number of days ({value.GetRawText()})");
}
