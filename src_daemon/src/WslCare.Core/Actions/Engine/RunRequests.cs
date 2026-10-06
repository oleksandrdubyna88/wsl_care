using WslCare.Core.Config;
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

    /// <summary>The processes A18's preview showed and the person confirmed, as <c>pid:start</c> (E7.S2b review A-H1); empty for
    /// every other request.</summary>
    public IReadOnlyList<string> ShownProcesses { get; init; } = [];

    /// <summary>The boot the request was written in (<c>/proc/sys/kernel/random/boot_id</c>, E6.S1 review D3); empty when the
    /// writer could not tell. A request of another boot is stale at once.</summary>
    public string BootId { get; init; } = string.Empty;

    /// <summary>The system-wide monotonic clock (ms since boot) when the request was written (E6.S1 review D3): within one boot
    /// the sweep ages a request by THIS, never by the wall clock a step can move. Meaningful only with <see cref="BootId"/>.</summary>
    public long CreatedMonotonicMs { get; init; }

    /// <summary>
    /// The ONE terminal line of a requested run that never did its work (plan §15o) — refused, cut off before it started, or
    /// swept: the request's kind, and per-action results only — an act's asked ids each marked <paramref name="status"/>, a full
    /// check NONE (its <c>["collect"]</c> names the run, not an action), and a kind this build does not know neither a kind nor
    /// rows — its "actions" would be guesses (§15o review G2). Shared by <c>DetachedRuns</c> and <see cref="RequestSweep"/>, so
    /// the three writers cannot shape it apart.
    /// </summary>
    public RunRecord TerminalLine(DateTimeOffset startedAt, DateTimeOffset endedAt, RunOutcome outcome, string status, string reason)
    {
        var kind = RunKinds.OfRequest(Kind);
        IReadOnlyList<ActionRecord> actions = kind == RunKind.Act ? [.. Actions.Select(a => new ActionRecord(a, 0, 0) { Status = status })] : [];
        return new RunRecord(Core.SchemaVersion.Current, RunId, Trigger, startedAt, endedAt, outcome, actions, kind) { Reason = reason };
    }
}

/// <summary>
/// The answer of a command that hands a run to systemd (E6.S1): <c>act … --detach</c> / <c>collect --detach</c> —
/// <c>accepted</c>, the run id the run will record itself under and its unit (plan §15j B2) — and <c>act --stop</c> —
/// <c>stopping</c>, the unit systemd was asked to stop. Its own <c>schemaVersion</c> 1.
/// </summary>
/// <param name="Result"><c>accepted</c> or <c>stopping</c>.</param>
/// <param name="Kind"><c>act</c>, <c>collect</c> or <c>stop</c>.</param>
public sealed record HandOffReport(int SchemaVersion, string Result, string Kind, string RunId, string Unit)
{
    public string? ProductVersion { get; init; }
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
    public static int MaxRequestBytes => Tuning.Current.Int(ConfigKeys.Requests.MaxBytes);

    /// <summary>How many request files one reader opens at most: a queue that deep is already a defect to report, and every
    /// unprivileged <c>status</c> reads them.</summary>
    public static int MaxRequestsRead => Tuning.Current.Int(ConfigKeys.Requests.MaxRead);

    /// <summary>The growth budget (plan §15k #8 + #17): at most 32 requests wait at once — ≤ 32 MiB in the folder — and a 33rd
    /// is refused at <c>--detach</c> with its own exit code. Every terminal path removes its request, the sweep included.</summary>
    public static int MaxQueued => Tuning.Current.Int(ConfigKeys.Requests.MaxQueued);

    private const string Extension = ".json";

    private const string Action = "run-request";

    public static string Directory(IHostPaths paths) => paths.Rules.Join(paths.StateDirectory, Folder);

    public static string File(IHostPaths paths, RunId runId) => paths.Rules.Join(Directory(paths), runId.Text + Extension);

    /// <summary>
    /// Writes a request for root's template unit to act on (E6.S1, plan §15k #1 / #14): EXCLUSIVELY — temporary sibling, then
    /// linked to <c>&lt;runId&gt;.json</c>, which fails if the name exists — 0644 in a folder made 0755, so a reader sees the
    /// whole request or none and a second writer never replaces it.
    /// </summary>
    public static ExclusiveCreate Create(IHostPaths paths, IFileSystem files, RunRequestFile request) =>
        files.CreateFileExclusively(File(paths, request.RunId), JsonSerializer.SerializeToUtf8Bytes(request, WslCareJsonContext.Default.RunRequestFile), Scope(paths));

    /// <summary>Removes the request of <paramref name="runId"/> when it is there (every terminal path); empty when it went or
    /// was not there, otherwise why not.</summary>
    public static string Remove(IHostPaths paths, IFileSystem files, RunId runId)
    {
        var path = File(paths, runId);
        try
        {
            return !files.FileExists(path) || files.DeleteFile(path, Scope(paths)).IsAllowed ? string.Empty : $"the request {path} could not be removed (refused by the deletion policy)";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"the request {path} could not be removed ({e.Message})";
        }
    }

    /// <summary>How many files of the folder are named for a run — what the budget counts (unread: a broken one counts too).</summary>
    public static int Count(IHostPaths paths, IFileSystem files)
    {
        try
        {
            return files.ListFiles(Directory(paths)).Count(path => FiledRunId(path) is not null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return MaxQueued;
        }
    }

    private static Files.Deletion.DeletionScope Scope(IHostPaths paths) => new(Directory(paths), Action);

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

    /// <summary>What the polled running block needs (coai E6 code round #7): how many requests are filed, and the OLDEST read —
    /// ordered and counted by FILE NAME (a run id starts with its UTC second, so ordinal order is time order; only
    /// <c>&lt;runId&gt;.json</c> counts), the oldest read through the hardened reader, and the next only when the oldest cannot be
    /// used. Never every file: a status poll does not read up to 32 MiB. The full read stays with the root sweep and
    /// <c>act --request</c>.</summary>
    public static (IReadOnlyList<RunRequestRead> Oldest, int Count) Peek(IHostPaths paths, IFileSystem files)
    {
        IReadOnlyList<(string Path, RunId Id)> filed;
        try
        {
            filed = [.. files.ListFiles(Directory(paths)).SelectMany(path => FiledRunId(path) is { } id ? [(path, id)] : Array.Empty<(string, RunId)>()).OrderBy(f => f.Item1, StringComparer.Ordinal)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ([new RunRequestRead.Bad(Directory(paths), $"the request folder cannot be listed ({e.Message})")], 0);
        }

        var read = new List<RunRequestRead>();
        foreach (var (path, id) in filed)
        {
            if (Read(files, path, id) is not { } one)
            {
                continue;
            }

            read.Add(one);
            if (one is RunRequestRead.Parsed || read.Count == 2)
            {
                break;
            }
        }

        return (read, filed.Count);
    }

    /// <summary>The request filed under <paramref name="runId"/>; <c>null</c> when there is none (or it vanished as it was read).</summary>
    public static RunRequestRead? Find(IHostPaths paths, IFileSystem files, RunId runId) => Read(files, File(paths, runId), runId);

    /// <summary>The run id a request file is named for; <c>null</c> for any other name (a temporary file, the folder itself).</summary>
    public static RunId? FiledRunId(string path)
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
                { RunId: not null, Kind: not null, Actions: not null } file when file.RunId == filedAs => Validated(path, file with { Shown = file.Shown ?? [], ShownProcesses = file.ShownProcesses ?? [], BootId = file.BootId ?? string.Empty }),
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
        : !KnownTrigger(file) ? $"names the trigger {file.Trigger}, which root never writes for a {file.Kind} request"
        : !KnownActions(file) ? $"names an action this daemon does not know for a {file.Kind} request"
        : !ValidShown(file.Shown) ? $"carries a shown list that is not at most {ShownList.MaxNames} anonymous volume names (64 lowercase hex digits)"
        : !ValidShownProcesses(file.ShownProcesses) ? $"carries a shown process list that is not at most {ShownList.MaxNames} pid:start keys"
        : string.Empty;

    /// <summary>What root writes (E6.S1 review S2): a detached act is the panel's (<c>manual</c>) or a terminal's (<c>cli</c>), a
    /// detached collect is the panel's — never <c>timer</c>, whose full run ACTS (a <c>collect</c> request marked timer would make
    /// <c>act --request</c> run the timer pass).</summary>
    private static bool KnownTrigger(RunRequestFile file) =>
        file.Kind == "collect" ? file.Trigger == RunTrigger.Manual : file.Trigger is RunTrigger.Manual or RunTrigger.Cli;

    private static bool KnownActions(RunRequestFile file) =>
        file.Kind == "collect" ? file.Actions.SequenceEqual(["collect"]) : file.Actions.Count > 0 && file.Actions.All(a => ActionId.Find(a) is not null);

    private static bool ValidShown(IReadOnlyList<string> shown) => shown.Count <= ShownList.MaxNames && shown.All(Docker.DockerJson.IsFullId);

    private static bool ValidShownProcesses(IReadOnlyList<string> shown) => shown.Count <= ShownList.MaxNames && shown.All(Suspects.SuspectSignals.IsShownKey);
}
