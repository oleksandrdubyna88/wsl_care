using WslCare.Core.Config;
using System.Globalization;

using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;
using WslCare.Core.Records;

namespace WslCare.Core.Actions.Engine;

/// <summary>
/// <c>{state}/stops/&lt;runId&gt;</c> (E6.S1, plan §15k #18): that <c>act --stop</c> asked systemd to stop a wedged run, and
/// when. A stop is SIGTERM, then — after <c>TimeoutStopSec=90</c> — SIGKILL, and a killed run records nothing: its
/// <c>running.json</c> stays behind, and the next root run's sweep (<see cref="RunningSweep"/>) finds this marker and records
/// the run <c>interrupted</c> with the true reason instead of a bare "pid gone". Root writes it; the request sweep removes a
/// marker once its run has a history line, or after a day.
/// </summary>
public static class StopMarkers
{
    public const string Folder = "stops";

    public static TimeSpan KeptFor => Tuning.Current.Hours(ConfigKeys.Runs.StopMarkerRetentionHours);

    private const string Action = "stop-marker";

    public static string Directory(IHostPaths paths) => paths.Rules.Join(paths.StateDirectory, Folder);

    public static string File(IHostPaths paths, RunId runId) => paths.Rules.Join(Directory(paths), runId.Text);

    /// <summary>Records that a stop was asked for <paramref name="runId"/> at <paramref name="at"/> through <paramref name="unit"/>.</summary>
    public static DeletionVerdict Mark(IHostPaths paths, IFileSystem files, RunId runId, string unit, DateTimeOffset at)
    {
        files.CreateDirectory(Directory(paths));
        var text = string.Create(CultureInfo.InvariantCulture, $"{at.UtcDateTime:O} {unit}\n");
        return files.WriteFileAtomically(File(paths, runId), System.Text.Encoding.UTF8.GetBytes(text), new DeletionScope(Directory(paths), Action));
    }

    /// <summary>The reason a swept run whose stop was asked records; empty when no stop was asked for it.</summary>
    public static string StoppedReason(IHostPaths paths, IFileSystem files, RunId runId) =>
        files.ReadStateFile(File(paths, runId), Tuning.Current.Int(ConfigKeys.Stops.MaxMarkerBytes)) is FileReadResult.Content content
            ? $"stopped: act --stop asked systemd to stop it ({System.Text.Encoding.UTF8.GetString(content.Bytes).Trim()}) and it did not exit within {Tuning.Current.Text(ConfigKeys.Units.StopTimeoutSeconds)} s of SIGTERM, so systemd killed it before it could record itself"
            : string.Empty;

    /// <summary>Removes the marker of <paramref name="runId"/> when there is one.</summary>
    public static void Remove(IHostPaths paths, IFileSystem files, RunId runId)
    {
        var path = File(paths, runId);
        if (files.FileExists(path))
        {
            files.DeleteFile(path, new DeletionScope(Directory(paths), Action));
        }
    }

    /// <summary>The run ids that have a marker.</summary>
    public static IReadOnlyList<RunId> List(IHostPaths paths, IFileSystem files) =>
        [.. files.ListFiles(Directory(paths)).SelectMany(path => RunId.TryParse(Path.GetFileName(path)) is { } id ? [id] : Array.Empty<RunId>())];
}
