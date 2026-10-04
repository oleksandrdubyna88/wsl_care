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

        return [.. names.SelectMany(path => FiledRunId(path) is { } id ? [Read(files, path, id)] : Array.Empty<RunRequestRead>())];
    }

    /// <summary>The request filed under <paramref name="runId"/>; <c>null</c> when there is none.</summary>
    public static RunRequestRead? Find(IHostPaths paths, IFileSystem files, RunId runId)
    {
        var path = File(paths, runId);
        return files.FileExists(path) ? Read(files, path, runId) : null;
    }

    private static RunId? FiledRunId(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(Extension, StringComparison.Ordinal) ? RunId.TryParse(name[..^Extension.Length]) : null;
    }

    private static RunRequestRead Read(IFileSystem files, string path, RunId filedAs) => files.ReadFile(path) switch
    {
        FileReadResult.Content content => Parse(path, content.Bytes, filedAs),
        FileReadResult.Unreadable u => new RunRequestRead.Bad(path, $"cannot be read ({u.Reason})"),
        _ => new RunRequestRead.Bad(path, "disappeared while it was read"),
    };

    private static RunRequestRead Parse(string path, byte[] bytes, RunId filedAs)
    {
        try
        {
            return JsonSerializer.Deserialize(bytes, WslCareJsonContext.Default.RunRequestFile) switch
            {
                { RunId: not null, Kind: not null, Actions: not null } file when file.RunId == filedAs => new RunRequestRead.Parsed(path, file with { Shown = file.Shown ?? [] }),
                { RunId: not null } other => new RunRequestRead.Bad(path, $"names run {other.RunId} but is filed as {filedAs}"),
                _ => new RunRequestRead.Bad(path, "does not parse: it is not a run request"),
            };
        }
        catch (JsonException e)
        {
            return new RunRequestRead.Bad(path, $"does not parse ({e.Message})");
        }
    }
}
