using System.Text.Json;

using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Json;

namespace WslCare.Core.Records;

/// <summary>
/// Appends one <see cref="RunRecord"/> per run to <c>history.jsonl</c> in the state directory
/// (plan §6), through <see cref="IFileSystem.AppendLine"/> so two processes finishing together
/// write two whole lines. Retention (90 days) is E2.S3's sweep.
/// </summary>
public sealed class RunRecordWriter(IHostPaths paths, IFileSystem files)
{
    public const string FileName = "history.jsonl";

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    public string HistoryFile => Path.Combine(paths.StateDirectory, FileName);

    public void Append(RunRecord record)
    {
        files.CreateDirectory(paths.StateDirectory);
        files.AppendLine(HistoryFile, JsonSerializer.Serialize(record, WslCareJsonContext.Compact.RunRecord), LockTimeout);
    }
}
