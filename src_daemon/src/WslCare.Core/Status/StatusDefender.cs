using WslCare.Core.Collect;
using WslCare.Core.Collectors;
using WslCare.Core.Thresholds;

namespace WslCare.Core.Status;

/// <summary>
/// E15.S10: <c>status --json</c>'s <c>defender</c> — Windows Defender's CPU exactly as the newest full run recorded it in its detail
/// (read with its verdicts, one file), with the basis a carried verdict has: <c>fullRun</c>, the run, when it ended, how old it is.
/// <c>status</c> starts no process, so it never reads the counters itself. The script's own run time stays in the run detail: it moves at every run
/// and is no part of what <c>status</c> carries about Defender.
/// </summary>
public static class StatusDefender
{
    /// <summary>Why the Windows binary's <c>status</c> carries no Defender figure.</summary>
    public const string ReadByTheDistro = "the distro's full run reads Windows Defender's CPU (the third fixed script runs from inside the distro); the distro's status --json carries it as defender";

    public static DefenderReport From(Reading<RecordedVerdicts> fullRun, DateTimeOffset now) => fullRun switch
    {
        Reading<RecordedVerdicts>.Available { Value: var run } => Carried(run, now),
        _ => DefenderReports.Unavailable(fullRun.ReasonOrEmpty) with { Basis = new VerdictBasis(VerdictSource.FullRun, null, null, null) },
    };

    /// <summary>The Windows binary's answer: unavailable, naming the side that reads it.</summary>
    public static DefenderReport OnWindows => DefenderReports.Unavailable(ReadByTheDistro);

    private static DefenderReport Carried(RecordedVerdicts run, DateTimeOffset now)
    {
        var basis = run.BasisAt(now);
        return run.Defender switch
        {
            Reading<DefenderReport>.Available { Value: var defender } => defender with { Basis = basis, ElapsedSeconds = null },
            var none => DefenderReports.Unavailable(none.ReasonOrEmpty) with { Basis = basis },
        };
    }
}
