using System.Globalization;
using System.Text.Json;

using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;
using WslCare.Core.Json;

namespace WslCare.Core.Events;

/// <summary>
/// <c>{state}/container-starts/{yyyy-MM-dd}.jsonl</c> (plan §4.3, §6): one line per container start and the
/// follower's markers, filed under the UTC day of the line's instant, appended through
/// <see cref="IFileSystem.AppendLine"/> (whole lines under the cross-process lock), kept 14 days.
/// </summary>
public sealed class ContainerStartsStore(IHostPaths paths, IFileSystem files)
{
    public const string Folder = "container-starts";

    /// <summary>Plan §4.3, §6: kept 14 days.</summary>
    public const int RetentionDays = 14;

    private const string Action = "container-starts-retention";
    private const string Extension = ".jsonl";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    public string Directory => paths.Rules.Join(paths.StateDirectory, Folder);

    /// <summary>The follower's own lock: one follower at a time (an exclusive open, released by the OS when it dies).</summary>
    public string FollowerLock => paths.Rules.Join(paths.StateDirectory, "events-follower.lock");

    public void Append(CoverageLine line)
    {
        files.CreateDirectory(Directory);
        files.AppendLine(DayFile(line.Instant), JsonSerializer.Serialize(CoverageLineJson.Of(line), WslCareJsonContext.Compact.CoverageLineJson), LockTimeout);
    }

    /// <summary>Every line of every kept day file, in file order (day, then append order); unparseable lines skipped.</summary>
    public IReadOnlyList<CoverageLine> ReadAll() =>
        [.. files.ListFiles(Directory)
            .Where(f => DayOf(f) is not null)
            .Order(StringComparer.Ordinal)
            .SelectMany(Lines)];

    /// <summary>Day files older than <see cref="RetentionDays"/> removed through <see cref="IFileSystem"/> (scope: the
    /// folder itself); a refusal or a failure is reported, never thrown.</summary>
    public IReadOnlyList<string> Prune(DateTimeOffset now)
    {
        var cutoff = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-RetentionDays);
        var scope = new DeletionScope(Directory, Action);
        var problems = new List<string>();
        foreach (var file in files.ListFiles(Directory).Where(f => DayOf(f) is { } day && day < cutoff))
        {
            try
            {
                if (files.DeleteFile(file, scope) is DeletionVerdict.Refused refused)
                {
                    problems.Add(refused.Reason);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{file}: {e.Message}");
            }
        }

        return problems;
    }

    private string DayFile(DateTimeOffset instant) =>
        paths.Rules.Join(Directory, instant.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + Extension);

    private static DateOnly? DayOf(string file)
    {
        var name = Path.GetFileName(file);
        return name.EndsWith(Extension, StringComparison.Ordinal)
            && DateOnly.TryParseExact(name[..^Extension.Length], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : null;
    }

    private IEnumerable<CoverageLine> Lines(string file) => files.ReadFile(file) switch
    {
        FileReadResult.Content content => System.Text.Encoding.UTF8.GetString(content.Bytes).Split('\n').Where(l => l.Trim().Length > 0).SelectMany(Parse),
        _ => [],
    };

    private static IEnumerable<CoverageLine> Parse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize(line, WslCareJsonContext.Compact.CoverageLineJson) is { Kind: not null } json ? json.ToLine() : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
