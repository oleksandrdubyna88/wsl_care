using System.Text.Json;

using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Records;

namespace WslCare.Core.Actions.Engine;

/// <summary>
/// <c>{state}/requests/&lt;runId&gt;.json</c> (plan §15j B2): a run the panel asked for and the daemon accepted but has not
/// started yet — the persisted QUEUED state. Written (0644, by root) by E6.S1's <c>act … --detach</c> / <c>collect
/// --detach</c> before it starts the template unit, removed by the run it starts or by the ownership-checked sweep; READ
/// here, by <c>status</c> and <c>runs show</c>, which never write or remove one.
/// </summary>
/// <param name="Kind"><c>act</c> or <c>collect</c>.</param>
/// <param name="Actions">The action ids asked for (an <c>act</c>), or <c>["collect"]</c>.</param>
/// <param name="CreatedAt">When the request was written, UTC.</param>
public sealed record RunRequestFile(int SchemaVersion, RunId RunId, string Kind, IReadOnlyList<string> Actions, RunTrigger Trigger, DateTimeOffset CreatedAt)
{
    /// <summary>The names A4's preview showed and the person confirmed (E6.S1: <c>--only -</c>, persisted here); empty for
    /// every other request.</summary>
    public IReadOnlyList<string> Shown { get; init; } = [];
}

/// <summary>One request file as read: parsed, or why it cannot be used — a closed set.</summary>
public abstract record RunRequestRead
{
    private RunRequestRead()
    {
    }

    /// <summary>A request that parses and is filed under its own run id.</summary>
    public sealed record Parsed(string Path, RunRequestFile File) : RunRequestRead;

    /// <summary>A request that cannot be read, does not parse, or is filed under another run's name — reported, never
    /// guessed at and never removed by a reader.</summary>
    public sealed record Bad(string Path, string Why) : RunRequestRead;
}

/// <summary>Reads the request folder — read-only: nothing here writes, moves or removes a request.</summary>
public static class RunRequests
{
    public const string Folder = "requests";

    /// <summary>The largest request read: a full shown list (10 000 names of 64 hex digits, quoted, comma-separated ≈ 670 KB)
    /// with room to spare; one byte more is a refusal, whatever the file's length claims.</summary>
    public const int MaxRequestBytes = 1024 * 1024;

    /// <summary>How many request files one reader opens at most: a queue that deep is already a defect to report, and every
    /// unprivileged <c>status</c> reads them.</summary>
    public const int MaxRequestsRead = 64;

    private const string Extension = ".json";

    public static string Directory(IHostPaths paths) => paths.Rules.Join(paths.StateDirectory, Folder);

    public static string File(IHostPaths paths, RunId runId) => paths.Rules.Join(Directory(paths), runId.Text + Extension);

    /// <summary>Every file of the folder named <c>&lt;runId&gt;.json</c>, read — in ordinal (= run id) order; a temporary
    /// file or a stray name is not a request. A folder that cannot be listed is one <see cref="RunRequestRead.Bad"/>
    /// entry; a folder that does not exist is none.</summary>
    public static IReadOnlyList<RunRequestRead> List(IHostPaths paths, IFileSystem files)
    {
        IReadOnlyList<string> names;
        try
        {
            names = files.ListFiles(Directory(paths));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [new RunRequestRead.Bad(Directory(paths), $"the request folder cannot be listed ({e.Message})")];
        }

        var filed = names.SelectMany(path => FiledRunId(path) is { } id ? [(path, id)] : Array.Empty<(string, RunId)>()).ToList();
        var reads = filed.Take(MaxRequestsRead).SelectMany(f => Read(files, f.Item1, f.Item2) is { } read ? [read] : Array.Empty<RunRequestRead>());
        return filed.Count > MaxRequestsRead
            ? [.. reads, new RunRequestRead.Bad(Directory(paths), $"holds {filed.Count - MaxRequestsRead} more requests than the {MaxRequestsRead} a reader opens; they were not read")]
            : [.. reads];
    }

    /// <summary>The request filed under <paramref name="runId"/>; <c>null</c> when there is none (or it vanished as it was read).</summary>
    public static RunRequestRead? Find(IHostPaths paths, IFileSystem files, RunId runId) => Read(files, File(paths, runId), runId);

    private static RunId? FiledRunId(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(Extension, StringComparison.Ordinal) ? RunId.TryParse(name[..^Extension.Length]) : null;
    }

    /// <summary>One request, read as a state file root wrote (regular, no link, capped, root's alone — E6.S0 review S1); a
    /// file gone by the time it is read is no request (review D4: the run it named has just moved on).</summary>
    private static RunRequestRead? Read(IFileSystem files, string path, RunId filedAs) => files.ReadStateFile(path, MaxRequestBytes) switch
    {
        FileReadResult.Content content => Parse(path, content.Bytes, filedAs),
        FileReadResult.Unreadable u => new RunRequestRead.Bad(path, $"cannot be used ({u.Reason})"),
        _ => null,
    };

    private static RunRequestRead Parse(string path, byte[] bytes, RunId filedAs)
    {
        try
        {
            return JsonSerializer.Deserialize(bytes, WslCareJsonContext.Default.RunRequestFile) switch
            {
                { RunId: not null, Kind: not null, Actions: not null } file when file.RunId == filedAs => Validated(path, file with { Shown = file.Shown ?? [] }),
                { RunId: not null } other => new RunRequestRead.Bad(path, $"names run {other.RunId} but is filed as {filedAs}"),
                _ => new RunRequestRead.Bad(path, "does not parse: it is not a run request"),
            };
        }
        catch (JsonException e)
        {
            return new RunRequestRead.Bad(path, $"does not parse ({e.Message})");
        }
    }

    /// <summary>Parsed is not trusted: the content is held to exactly what root writes — schema 1, a known kind, known ids
    /// (an <c>act</c>'s action ids; a <c>collect</c>'s <c>["collect"]</c>), every shown name a 64-hex volume name, no more of
    /// them than a shown list carries (E6.S0 review S1).</summary>
    private static RunRequestRead Validated(string path, RunRequestFile file) =>
        ContentProblem(file) is { Length: > 0 } problem ? new RunRequestRead.Bad(path, problem) : new RunRequestRead.Parsed(path, file);

    private static string ContentProblem(RunRequestFile file) =>
        file.SchemaVersion != SchemaVersion.Current ? $"has schemaVersion {file.SchemaVersion}, not {SchemaVersion.Current}"
        : file.Kind is not ("act" or "collect") ? "names a kind that is neither act nor collect"
        : !KnownActions(file) ? $"names an action this daemon does not know for a {file.Kind} request"
        : !ValidShown(file.Shown) ? $"carries a shown list that is not at most {ShownList.MaxNames} anonymous volume names (64 lowercase hex digits)"
        : string.Empty;

    private static bool KnownActions(RunRequestFile file) =>
        file.Kind == "collect" ? file.Actions.SequenceEqual(["collect"]) : file.Actions.Count > 0 && file.Actions.All(a => ActionId.Find(a) is not null);

    private static bool ValidShown(IReadOnlyList<string> shown) => shown.Count <= ShownList.MaxNames && shown.All(Docker.DockerJson.IsFullId);
}
