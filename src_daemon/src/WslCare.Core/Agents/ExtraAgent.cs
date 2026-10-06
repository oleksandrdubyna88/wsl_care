using WslCare.Core.Config;
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

    /// <summary>The prefix of every manual agent's id.</summary>
    public const string IdPrefix = "manual:";

    /// <summary>The id this entry answers under in <c>agents list</c> and <c>archive.agents</c> — never a catalogue id.</summary>
    public string Id => $"{IdPrefix}{Name}";
}

/// <summary>A list of manual agents read from JSON, or why not (the first problem, named).</summary>
public sealed record ExtraList(IReadOnlyList<ExtraAgent> Agents, string Problem)
{
    public static ExtraList Invalid(string problem) => new([], problem);
}

/// <summary>One entry read: valid, or the problem with it.</summary>
public abstract record ExtraEntryRead
{
    private ExtraEntryRead()
    {
    }

    public sealed record Ok(ExtraAgent Agent) : ExtraEntryRead;

    public sealed record Bad(string Problem) : ExtraEntryRead;
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

    /// <summary>Whether <paramref name="name"/> has a manual agent's name shape (what <c>archive.agents</c>' <c>manual:</c> takes).</summary>
    public static bool IsName(string name) => Matches(name, NameExpression);

    public const string Describe = "a list of at most 16 AI agents, each {\"cli\", \"side\": \"wsl\" or \"windows\", \"name\", \"dataFolders\": 1 to 8 absolute paths, \"sessionGlob\"} (config set aiAgents.extra - reads it from stdin)";

    private static readonly string[] Members = ["cli", "side", "name", "dataFolders", "sessionGlob"];

    /// <summary>The entries of <paramref name="value"/>, or why it is not a valid list (the first problem, named).</summary>
    public static ExtraList Read(JsonElement value) =>
        value.ValueKind != JsonValueKind.Array ? ExtraList.Invalid("must be a JSON list")
        : value.GetArrayLength() > MaxEntries ? ExtraList.Invalid($"holds {value.GetArrayLength()} entries; at most {MaxEntries}")
        : Entries([.. value.EnumerateArray().Select(ReadOne)]);

    /// <summary>Every entry valid and each name once — or the first problem, named by the entry it is in.</summary>
    private static ExtraList Entries(IReadOnlyList<ExtraEntryRead> reads) =>
        reads.Select((r, i) => (Read: r, Index: i)).FirstOrDefault(x => x.Read is ExtraEntryRead.Bad) is { Read: ExtraEntryRead.Bad bad } first
            ? ExtraList.Invalid($"entry {first.Index + 1}: {bad.Problem}")
            : Named([.. reads.OfType<ExtraEntryRead.Ok>().Select(o => o.Agent)]);

    private static ExtraList Named(IReadOnlyList<ExtraAgent> agents) =>
        agents.GroupBy(a => a.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice
            ? ExtraList.Invalid($"the name \"{twice.Key}\" is used twice")
            : new ExtraList(agents, string.Empty);

    private static ExtraEntryRead ReadOne(JsonElement entry) =>
        entry.ValueKind != JsonValueKind.Object ? new ExtraEntryRead.Bad("must be an object")
        : entry.EnumerateObject().Select(p => p.Name).FirstOrDefault(n => !Members.Contains(n, StringComparer.Ordinal)) is { } unknown ? new ExtraEntryRead.Bad($"unknown member \"{Printable(unknown)}\"")
        : !HasStringMembers(entry) ? new ExtraEntryRead.Bad("cli, side, name and sessionGlob must be text, dataFolders a list of text")
        : Checked(new ExtraAgent(Text(entry, "cli"), Text(entry, "side"), Text(entry, "name"), Folders(entry), Text(entry, "sessionGlob")));

    private static ExtraEntryRead Checked(ExtraAgent agent) =>
        Problem(agent) is { Length: > 0 } problem ? new ExtraEntryRead.Bad(problem) : new ExtraEntryRead.Ok(agent);

    /// <summary>Why <paramref name="agent"/> is not a valid entry; empty when it is — the first rule it breaks.</summary>
    public static string Problem(ExtraAgent agent) =>
        new Func<string>[]
        {
            () => agent.Side is Wsl or Windows ? string.Empty : $"side must be \"{Wsl}\" or \"{Windows}\"",
            () => NameProblem(agent.Name),
            () => PathProblem(agent.Cli, agent.Side) is { Length: > 0 } cli ? $"cli {cli}" : string.Empty,
            () => FoldersProblem(agent),
            () => GlobProblem(agent.SessionGlob),
        }.Select(rule => rule()).FirstOrDefault(p => p.Length > 0) ?? string.Empty;

    private static readonly string[] TextMembers = ["cli", "side", "name"];

    private static bool HasStringMembers(JsonElement entry) =>
        TextMembers.All(m => IsText(entry, m)) && (!entry.TryGetProperty("sessionGlob", out _) || IsText(entry, "sessionGlob")) && IsTextList(entry, "dataFolders");

    private static bool IsText(JsonElement entry, string member) => entry.TryGetProperty(member, out var v) && v.ValueKind == JsonValueKind.String;

    private static bool IsTextList(JsonElement entry, string member) =>
        entry.TryGetProperty(member, out var v) && v.ValueKind == JsonValueKind.Array && v.EnumerateArray().All(f => f.ValueKind == JsonValueKind.String);

    private static string NameProblem(string name) =>
        Matches(name, NameExpression) ? string.Empty : $"name must be 1 to {MaxNameLength} letters, digits, spaces, '.', '_', '+' or '-', starting with a letter or a digit";

    private static string FoldersProblem(ExtraAgent agent) =>
        agent.DataFolders.Count is < 1 or > MaxFolders ? $"dataFolders must hold 1 to {MaxFolders} folders"
        : agent.DataFolders.Select(f => PathProblem(f, agent.Side)).FirstOrDefault(p => p.Length > 0) is { } bad ? $"a data folder {bad}"
        : string.Empty;

    /// <summary>One rule of a path's shape: what it refuses, in the words of the refusal.</summary>
    private sealed record PathRule(Func<string, string, bool> Refuses, Func<string, string> Says);

    private static readonly PathRule[] PathShapeRules =
    [
        new((p, _) => p.Length is 0 or > MaxPathLength, _ => $"must be 1 to {MaxPathLength} characters"),
        new((p, _) => p.Any(char.IsControl), _ => "holds a control character"),
        new((p, _) => p.StartsWith('-'), _ => "starts with '-'"),
        new((p, side) => !(side == Windows ? IsDrivePath(p) : p.StartsWith('/')), side => side == Windows ? "must be an absolute path X:\\…" : "must be an absolute path /…"),
        new((p, _) => p.Split('/', '\\').Any(s => s is ".." or "."), _ => "holds a . or .. segment"),
    ];

    /// <summary>Why <paramref name="path"/> is not an absolute path of <paramref name="side"/>; empty when it is.</summary>
    public static string PathProblem(string path, string side) =>
        PathShapeRules.FirstOrDefault(r => r.Refuses(path, side)) is { } broken ? broken.Says(side) : string.Empty;

    /// <summary>Empty, or relative segments of letters, digits, '.', '_', '-', '*', and AT MOST ONE whole <c>**</c> segment (review R4:
    /// a second one would count a session once per way down to it).</summary>
    public static string GlobProblem(string glob) =>
        glob.Length == 0 ? string.Empty
        : glob.Length > MaxGlobLength ? $"sessionGlob is longer than {MaxGlobLength} characters"
        : glob.Split('/').Count(s => s == "**") > 1 ? "sessionGlob may hold one '**' segment at most"
        : glob.Split('/').All(IsGlobSegment) ? string.Empty
        : "sessionGlob must be relative segments of letters, digits, '.', '_', '-' and '*', or a whole '**' segment — no '..', no leading '/'";

    private static bool IsGlobSegment(string segment) =>
        segment == "**" || (segment.Length > 0 && segment is not ("." or "..") && Matches(segment, GlobSegmentExpression));

    private static bool IsDrivePath(string value) => value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] == '\\';

    private static string Text(JsonElement entry, string member) =>
        entry.TryGetProperty(member, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static IReadOnlyList<string> Folders(JsonElement entry) =>
        entry.TryGetProperty("dataFolders", out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Select(f => f.ValueKind == JsonValueKind.String ? f.GetString() ?? string.Empty : string.Empty)]
            : [];

    private static string Printable(string text) => new([.. text.Take(40).Select(c => char.IsControl(c) ? '?' : c)]);

    /// <summary>A name: <see cref="MaxNameLength"/> characters at most (the schema's limit, spelt in the expression).</summary>
    private const string NameExpression = @"^[A-Za-z0-9][A-Za-z0-9 ._+-]{0,63}\z";

    private const string GlobSegmentExpression = @"^[A-Za-z0-9._*-]+\z";

    /// <summary>A match bounded by <c>patterns.matchTimeoutMilliseconds</c> — the text is the user's own.</summary>
    private static bool Matches(string text, string expression) =>
        Regex.IsMatch(text, expression, RegexOptions.CultureInvariant, Tuning.Current.Milliseconds(ConfigKeys.Patterns.MatchTimeoutMilliseconds));

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
