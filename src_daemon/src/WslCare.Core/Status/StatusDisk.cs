using WslCare.Core.Collect;
using WslCare.Core.Collectors;
using WslCare.Core.Thresholds;

namespace WslCare.Core.Status;

/// <summary>
/// E15.S2: <c>status --json</c>'s <c>disk</c> — the virtual disks and their hosting volumes exactly as the newest full run recorded
/// them in its detail (read with its verdicts, one file), with the basis a carried verdict has: <c>fullRun</c>, the run, when it
/// ended, how old it is. <c>status</c> starts no process, so it never reads them itself.
/// </summary>
public static class StatusDisk
{
    /// <summary>Why the Windows binary's <c>status</c> carries no virtual disks.</summary>
    public const string ReadByTheDistro = "the distro's full run reads the virtual disks (the Windows disk script runs from inside the distro); the distro's status --json carries them as disk";

    public static DiskReport From(Reading<RecordedVerdicts> fullRun, DateTimeOffset now) => fullRun switch
    {
        Reading<RecordedVerdicts>.Available { Value: var run } => Carried(run, now),
        _ => DiskReports.Unavailable(fullRun.ReasonOrEmpty) with { Basis = new VerdictBasis(VerdictSource.FullRun, null, null, null) },
    };

    /// <summary>The Windows binary's answer: unavailable, naming the side that reads them.</summary>
    public static DiskReport OnWindows => DiskReports.Unavailable(ReadByTheDistro);

    private static DiskReport Carried(RecordedVerdicts run, DateTimeOffset now)
    {
        var basis = new VerdictBasis(VerdictSource.FullRun, run.RunId.Text, run.EvaluatedAt, Math.Round((now - run.EvaluatedAt).TotalSeconds));
        return run.Disk switch
        {
            Reading<DiskReport>.Available { Value: var disk } => disk with { Basis = basis },
            var none => DiskReports.Unavailable(none.ReasonOrEmpty) with { Basis = basis },
        };
    }
}
