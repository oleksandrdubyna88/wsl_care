using WslCare.Core.Collect;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Records;

/// <summary>How a run's two records went: the detail file (relative to the state directory) when one was written.</summary>
public sealed record RecordResult(Recording Recording, string Reason, string DetailFile)
{
    /// <summary>Whether the history line was written (a failed DETAIL still gets a <c>failed</c> line).</summary>
    public bool LineWritten { get; init; } = true;
}

/// <summary>
/// The write order of plan §15b #1, for every kind of run (<c>collect</c>, <c>act</c>): the run detail FIRST (atomic:
/// temp + rename), then the history line that names it — or, when the detail failed, a <c>failed</c> line with the
/// reason. A failed write makes the run <c>failed</c> with the reason, never a silent success.
/// </summary>
public static class RunRecorder
{
    /// <param name="detailJson">The detail's bytes.</param>
    /// <param name="line">The history line, given the detail's relative path (empty when it could not be written) and
    /// the failure (empty when it was written).</param>
    public static RecordResult Record(IHostPaths paths, IFileSystem files, RunId runId, byte[] detailJson, Func<string, string, RunRecord> line)
    {
        var relative = RunDetailStore.RelativePath(runId);
        var failure = WriteDetail(paths, files, runId, detailJson);
        try
        {
            new RunRecordWriter(paths, files).Append(Line(line, relative, failure));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException)
        {
            return LineFailed(e, relative, failure);
        }

        return failure.Length == 0
            ? new RecordResult(Recording.Recorded, string.Empty, relative)
            : new RecordResult(Recording.Failed, failure, string.Empty);
    }

    /// <summary>The line naming the detail — or, when the detail failed, the line carrying why.</summary>
    private static RunRecord Line(Func<string, string, RunRecord> line, string relative, string failure) =>
        failure.Length == 0 ? line(relative, string.Empty) : line(string.Empty, failure);

    private static RecordResult LineFailed(Exception e, string relative, string failure) =>
        new(Recording.Failed, $"the history line could not be written ({e.Message}){(failure.Length > 0 ? $"; before that, {failure}" : string.Empty)}", failure.Length == 0 ? relative : string.Empty)
        {
            LineWritten = false,
        };

    /// <summary>Empty when written; otherwise why not.</summary>
    private static string WriteDetail(IHostPaths paths, IFileSystem files, RunId runId, byte[] json)
    {
        try
        {
            return RunDetailStore.Write(paths, files, runId, json) is Files.Deletion.DeletionVerdict.Refused refused
                ? $"the run detail could not be written: {refused.Reason}"
                : string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"the run detail could not be written: {e.Message}";
        }
    }
}
