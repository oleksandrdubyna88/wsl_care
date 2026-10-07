using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;
using WslCare.Core.Json;

namespace WslCare.Core.Records;

/// <summary>What a run detail file tells about its run even when nothing else of it is read — enough for the
/// reconcile to write the <c>interrupted</c> history line of a detail that has none (plan §15b #1).</summary>
public sealed record RunDetailHead(int SchemaVersion, RunId RunId, RunTrigger Trigger, DateTimeOffset StartedAt, DateTimeOffset EndedAt, bool DryRun)
{
    /// <summary>The detail's own <c>kind</c> member as it is on disk — <c>act</c> in an act's detail, absent in a full run's, and
    /// unknown for anything else, an explicit <c>null</c> included (PR #16 retro round G0: only a MISSING member is a full run's).
    /// Read through <see cref="Detail"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public RecordedKind Kind { get; init; }

    /// <summary>What the detail is (<see cref="RunKinds.OfDetail"/>, plan §15o).</summary>
    [JsonIgnore]
    public DetailKind Detail => RunKinds.OfDetail(Kind);
}

/// <summary>One detail file on disk: its path relative to the state directory, and the run it names.</summary>
public sealed record StoredDetail(string RelativePath, RunId RunId);

/// <summary>
/// <c>{state}/runs/{yyyy-MM-dd}/{runId}.json</c> (plan §6): the full detail of one run. Written FIRST of a run's
/// three records, atomically — a temporary file in the same folder and one rename, judged by the deletion policy
/// inside <c>runs/</c> — so a reader finds the whole detail or none (plan §15b #1).
/// </summary>
public static class RunDetailStore
{
    public const string Folder = "runs";
    private const string Action = "run-detail";
    private const string Extension = ".json";

    public static string Root(IHostPaths paths) => paths.Rules.Join(paths.StateDirectory, Folder);

    /// <summary>The detail's path relative to the state directory, <c>/</c>-separated (what the history line names);
    /// the day folder is the UTC day the run id carries.</summary>
    public static string RelativePath(RunId runId) =>
        $"{Folder}/{DayOf(runId).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}/{runId.Text}{Extension}";

    public static string Absolute(IHostPaths paths, string relativePath) =>
        paths.Rules.Join(paths.StateDirectory, relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Writes the detail; the verdict says whether the policy allowed it. I/O failures throw — the caller
    /// turns them into a <c>failed</c> run with the reason.</summary>
    public static DeletionVerdict Write(IHostPaths paths, IFileSystem files, RunId runId, ReadOnlySpan<byte> json)
    {
        var path = Absolute(paths, RelativePath(runId));
        files.CreateDirectory(Path.GetDirectoryName(path) ?? Root(paths));
        return files.WriteFileAtomically(path, json, new DeletionScope(Root(paths), Action));
    }

    /// <summary>Every detail file under <c>runs/</c> whose name is a run id (temporary files and strays are not details).</summary>
    public static IReadOnlyList<StoredDetail> List(IHostPaths paths, IFileSystem files) =>
        [.. files.ListDirectories(Root(paths))
            .SelectMany(day => files.ListFiles(day))
            .SelectMany(file => Detail(file))];

    /// <summary>The head of a stored detail; <c>null</c> when it cannot be read or is not a detail (a legitimate "not readable").</summary>
    public static RunDetailHead? ReadHead(IHostPaths paths, IFileSystem files, string relativePath)
    {
        if (files.ReadFile(Absolute(paths, relativePath), RootFileCaps.History) is not FileReadResult.Content content)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(content.Bytes, WslCareJsonContext.Default.RunDetailHead) is { RunId: not null } head ? head : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<StoredDetail> Detail(string file)
    {
        var name = Path.GetFileName(file);
        var day = Path.GetFileName(Path.GetDirectoryName(file) ?? string.Empty);
        return name.EndsWith(Extension, StringComparison.Ordinal) && RunId.TryParse(name[..^Extension.Length]) is { } id
            ? [new StoredDetail($"{Folder}/{day}/{name}", id)]
            : [];
    }

    internal static DateOnly DayOf(RunId runId) =>
        DateOnly.ParseExact(runId.Text[..8], "yyyyMMdd", CultureInfo.InvariantCulture);
}
