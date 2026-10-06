using System.Text.Json;

using WslCare.Core.Agents;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Archive;

/// <summary>An agent's own retention as this side found it (plan §15r D10).</summary>
/// <param name="Days">The days the agent keeps a session; <c>null</c> when it deletes nothing on its own (or none is known).</param>
/// <param name="From">Where it came from: a file, or the catalogue's documented default.</param>
/// <param name="Warnings">What a person must know: a retention of 0 (the agent keeps nothing), an unreadable value.</param>
public sealed record RetentionFound(int? Days, string From, IReadOnlyList<string> Warnings)
{
    public static RetentionFound None(string from) => new(null, from, []);
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

    /// <summary>The managed settings of Claude Code inside the distribution (Anthropic's documented place).</summary>
    public const string LinuxManaged = "/etc/claude-code/managed-settings.json";

    /// <summary>Where Claude Code's managed settings live on Windows, under <c>Program Files</c>.</summary>
    public const string WindowsManagedUnderProgramFiles = @"ClaudeCode\managed-settings.json";

    /// <summary>The files Claude Code's retention is read from on this side, in the order the first one holding the key wins.</summary>
    public static IReadOnlyList<string> ClaudeFiles(IHostPaths paths, Func<string, string?> environment) =>
    [
        paths is LinuxHostPaths linux ? linux.DistroPath(LinuxManaged) : Path.Combine(environment("ProgramFiles") ?? @"C:\Program Files", WindowsManagedUnderProgramFiles),
        Path.Combine(ConfigFolder(paths, environment), "settings.json"),
    ];

    /// <summary>Claude Code's configuration folder: <c>CLAUDE_CONFIG_DIR</c> when set, else <c>.claude</c> under the home.</summary>
    private static string ConfigFolder(IHostPaths paths, Func<string, string?> environment) =>
        (paths, environment("CLAUDE_CONFIG_DIR") ?? string.Empty) switch
        {
            (LinuxHostPaths linux, { } configured) when configured.StartsWith('/') => linux.DistroPath(configured),
            (WindowsHostPaths, { } configured) when PathRules.Windows.IsAbsolute(configured) => configured,
            _ => paths.Rules.Join(paths.Home, ".claude"),
        };

    /// <summary><paramref name="entry"/>'s own retention on this side.</summary>
    public static RetentionFound Read(AgentEntry entry, IHostPaths paths, IFileSystem files, Func<string, string?> environment) =>
        entry.Archive?.Retention is { Source: RetentionSources.ClaudeSettings } retention
            ? Claude(retention, paths, files, environment)
            : RetentionFound.None("none known: " + (entry.Archive?.Retention.Checked ?? "no archive block"));

    private static RetentionFound Claude(AgentRetention retention, IHostPaths paths, IFileSystem files, Func<string, string?> environment)
    {
        var read = ClaudeFiles(paths, environment).Select(path => (Path: path, Value: ValueIn(files, path))).ToList();
        var problems = read.Where(r => r.Value is ReadValue.Unusable).Select(r => $"{r.Path}: {((ReadValue.Unusable)r.Value).Why} — not taken").ToList();
        return read.FirstOrDefault(r => r.Value is ReadValue.Days) is { Path: { } path, Value: ReadValue.Days found }
            ? new RetentionFound(found.Value, path, [.. problems, .. ZeroWarning(found.Value)])
            : new RetentionFound(retention.DefaultDays, $"the documented default ({retention.DefaultDays} days): {ClaudeKey} is set in none of {string.Join(", ", read.Select(r => r.Path))}", problems);
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
