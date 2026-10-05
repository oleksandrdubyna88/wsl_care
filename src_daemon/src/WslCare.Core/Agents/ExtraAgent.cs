using System.Text.Json;
using System.Text.RegularExpressions;

namespace WslCare.Core.Agents;

/// <summary>
/// One manual AI agent of <c>aiAgents.extra</c> (plan §15q D4, R2): the CLI the person picked, which side it lives on, a name,
/// its data folders and — optionally — where its sessions are. ROOT reads only <see cref="DataFolders"/> and
/// <see cref="SessionGlob"/>, as data for the walk and the protected roots; <see cref="Cli"/> is never a file-system argument
/// in a root process.
/// </summary>
/// <param name="Side"><see cref="ExtraAgentShape.Wsl"/> or <see cref="ExtraAgentShape.Windows"/>.</param>
/// <param name="SessionGlob">Relative to the FIRST data folder; empty = sessions not counted.</param>
public sealed record ExtraAgent(string Cli, string Side, string Name, IReadOnlyList<string> DataFolders, string SessionGlob)
{
    public bool Equals(ExtraAgent? other) =>
        other is not null && Cli == other.Cli && Side == other.Side && Name == other.Name && SessionGlob == other.SessionGlob
        && DataFolders.SequenceEqual(other.DataFolders, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Cli, Side, Name, SessionGlob, DataFolders.Count);

    /// <summary>The id this entry answers under in <c>agents list</c> — never a catalogue id.</summary>
    public string Id => $"manual:{Name}";
}

/// <summary>
/// The SHAPE of <c>aiAgents.extra</c> (plan §15q R2.1), checked at <c>config set</c> AND at every load — what a value must look
/// like before anything looks at the disk. The filesystem rules (under the target user's real home, the same filesystem, no
/// overlap) are <see cref="ExtraAgentRules"/>'s, applied again at every root read.
/// </summary>
public static partial class ExtraAgentShape
{
    public const string Wsl = "wsl";
    public const string Windows = "windows";

    public const int MaxEntries = 16;
    public const int MaxFolders = 8;
    public const int MaxPathLength = 1024;
    public const int MaxGlobLength = 128;
    public const int MaxNameLength = 64;

    public const string Describe = "a list of at most 16 AI agents, each {\"cli\", \"side\": \"wsl\" or \"windows\", \"name\", \"dataFolders\": 1 to 8 absolute paths, \"sessionGlob\"} (config set aiAgents.extra - reads it from stdin)";

    private static readonly string[] Members = ["cli", "side", "name", "dataFolders", "sessionGlob"];

    /// <summary>The entries of <paramref name="value"/>, or why it is not a valid list (the first problem, named).</summary>
    public static (IReadOnlyList<ExtraAgent> Agents, string Problem) Read(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return ([], "must be a JSON list");
        }

        var entries = value.EnumerateArray().ToList();
        if (entries.Count > MaxEntries)
        {
            return ([], $"holds {entries.Count} entries; at most {MaxEntries}");
        }

        var agents = new List<ExtraAgent>();
        foreach (var (entry, index) in entries.Select((e, i) => (e, i)))
        {
            var (agent, problem) = ReadOne(entry);
            if (problem.Length > 0)
            {
                return ([], $"entry {index + 1}: {problem}");
            }

            agents.Add(agent!);
        }

        return agents.GroupBy(a => a.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice
            ? ([], $"the name \"{twice.Key}\" is used twice")
            : (agents, string.Empty);
    }

    private static (ExtraAgent? Agent, string Problem) ReadOne(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return (null, "must be an object");
        }

        if (entry.EnumerateObject().Select(p => p.Name).FirstOrDefault(n => !Members.Contains(n, StringComparer.Ordinal)) is { } unknown)
        {
            return (null, $"unknown member \"{Printable(unknown)}\"");
        }

        var agent = new ExtraAgent(Text(entry, "cli"), Text(entry, "side"), Text(entry, "name"), Folders(entry), Text(entry, "sessionGlob"));
        return (agent, Problem(agent, entry));
    }

    /// <summary>Why <paramref name="agent"/> is not a valid entry; empty when it is.</summary>
    public static string Problem(ExtraAgent agent) => Problem(agent, null);

    private static string Problem(ExtraAgent agent, JsonElement? raw) =>
        raw is { } r && !HasStringMembers(r) ? "cli, side, name and sessionGlob must be text, dataFolders a list of text"
        : agent.Side is not (Wsl or Windows) ? $"side must be \"{Wsl}\" or \"{Windows}\""
        : NameProblem(agent.Name) is { Length: > 0 } name ? name
        : PathProblem(agent.Cli, agent.Side) is { Length: > 0 } cli ? $"cli {cli}"
        : FoldersProblem(agent) is { Length: > 0 } folders ? folders
        : GlobProblem(agent.SessionGlob);

    private static bool HasStringMembers(JsonElement entry) =>
        new[] { "cli", "side", "name" }.All(m => entry.TryGetProperty(m, out var v) && v.ValueKind == JsonValueKind.String)
        && (!entry.TryGetProperty("sessionGlob", out var glob) || glob.ValueKind == JsonValueKind.String)
        && entry.TryGetProperty("dataFolders", out var folders) && folders.ValueKind == JsonValueKind.Array
        && folders.EnumerateArray().All(f => f.ValueKind == JsonValueKind.String);

    private static string NameProblem(string name) =>
        NamePattern().IsMatch(name) ? string.Empty : $"name must be 1 to {MaxNameLength} letters, digits, spaces, '.', '_', '+' or '-', starting with a letter or a digit";

    private static string FoldersProblem(ExtraAgent agent) =>
        agent.DataFolders.Count is < 1 or > MaxFolders ? $"dataFolders must hold 1 to {MaxFolders} folders"
        : agent.DataFolders.Select(f => PathProblem(f, agent.Side)).FirstOrDefault(p => p.Length > 0) is { } bad ? $"a data folder {bad}"
        : string.Empty;

    /// <summary>Why <paramref name="path"/> is not an absolute path of <paramref name="side"/>; empty when it is.</summary>
    public static string PathProblem(string path, string side) =>
        path.Length is 0 or > MaxPathLength ? $"must be 1 to {MaxPathLength} characters"
        : path.Any(char.IsControl) ? "holds a control character"
        : path.StartsWith('-') ? "starts with '-'"
        : !(side == Windows ? IsDrivePath(path) : path.StartsWith('/')) ? (side == Windows ? "must be an absolute path X:\\…" : "must be an absolute path /…")
        : path.Split('/', '\\').Any(s => s is ".." or ".") ? "holds a . or .. segment"
        : string.Empty;

    /// <summary>Empty, or relative segments of letters, digits, '.', '_', '-', '*', and whole <c>**</c> segments.</summary>
    public static string GlobProblem(string glob) =>
        glob.Length == 0 ? string.Empty
        : glob.Length > MaxGlobLength ? $"sessionGlob is longer than {MaxGlobLength} characters"
        : glob.Split('/').All(s => s == "**" || (s.Length > 0 && s is not ("." or "..") && GlobSegmentPattern().IsMatch(s))) ? string.Empty
        : "sessionGlob must be relative segments of letters, digits, '.', '_', '-' and '*', or a whole '**' segment — no '..', no leading '/'";

    private static bool IsDrivePath(string value) => value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] == '\\';

    private static string Text(JsonElement entry, string member) =>
        entry.TryGetProperty(member, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static IReadOnlyList<string> Folders(JsonElement entry) =>
        entry.TryGetProperty("dataFolders", out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Select(f => f.ValueKind == JsonValueKind.String ? f.GetString() ?? string.Empty : string.Empty)]
            : [];

    private static string Printable(string text) => new([.. text.Take(40).Select(c => char.IsControl(c) ? '?' : c)]);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._+-]{0,63}\z", RegexOptions.CultureInvariant, 250)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9._*-]+\z", RegexOptions.CultureInvariant, 250)]
    private static partial Regex GlobSegmentPattern();

    /// <summary>The list as the user layer holds it.</summary>
    public static void Write(IReadOnlyList<ExtraAgent> agents, Utf8JsonWriter writer)
    {
        writer.WriteStartArray();
        foreach (var agent in agents)
        {
            writer.WriteStartObject();
            writer.WriteString("cli", agent.Cli);
            writer.WriteString("side", agent.Side);
            writer.WriteString("name", agent.Name);
            writer.WriteStartArray("dataFolders");
            foreach (var folder in agent.DataFolders)
            {
                writer.WriteStringValue(folder);
            }

            writer.WriteEndArray();
            writer.WriteString("sessionGlob", agent.SessionGlob);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
