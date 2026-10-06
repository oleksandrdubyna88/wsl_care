using WslCare.Core.Config;
using System.Text.Json;

using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Json;

namespace WslCare.Core.Records;

/// <summary>
/// Appends one <see cref="RunRecord"/> per run to <c>history.jsonl</c> in the state directory
/// (plan §6), through <see cref="IFileSystem.AppendLine"/> so two processes finishing together
/// write two whole lines. Retention (90 days) is <see cref="RunRetention"/>.
/// </summary>
public sealed class RunRecordWriter(IHostPaths paths, IFileSystem files)
{
    public const string FileName = "history.jsonl";

    /// <summary>How long an append (or a retention rewrite) waits for the other writer's lock.</summary>
    public static TimeSpan LockTimeout => Tuning.Current.Seconds(ConfigKeys.Records.LockTimeoutSeconds);

    public string HistoryFile => HistoryFileIn(paths);

    /// <summary>Where <c>history.jsonl</c> is for a layout — the one rule every reader and writer uses.</summary>
    public static string HistoryFileIn(IHostPaths paths) => Path.Combine(paths.StateDirectory, FileName);

    public void Append(RunRecord record)
    {
        files.CreateDirectory(paths.StateDirectory);
        files.AppendLine(HistoryFile, JsonSerializer.Serialize(record, WslCareJsonContext.Compact.RunRecord), LockTimeout);
    }
}
