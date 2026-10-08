using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Status;
using WslCare.Core.Thresholds;

namespace WslCare.Core.Doctor;

/// <summary>
/// <c>doctor</c>'s two clock checks (PLAN_windows_time_guard.md D5): <c>windowsTime</c> and <c>clockReference</c>, read from
/// the NEWEST full run's recorded verdicts — one file read, no probe, so <c>doctor</c>'s worst case (the extension's
/// <c>worstCases.ts</c>) does not move. A critical verdict is a <c>problem</c>, a warning is <c>ok</c> with its words, no
/// full run or an unknown verdict is <c>unknown</c>. Only a WRONG Windows clock is critical, so a stopped-but-correct
/// <c>w32time</c> (a workgroup PC's default) never makes <c>doctor</c> unhealthy.
/// </summary>
public static class ClockChecks
{
    public const string WindowsTimeId = "windowsTime";
    public const string ClockReferenceId = "clockReference";

    public static IReadOnlyList<DoctorCheck> From(Reading<RecordedVerdicts> recorded, DateTimeOffset now) =>
        [Check(WindowsTimeId, ClockVerdicts.TimeServiceId, recorded, now), Check(ClockReferenceId, ClockVerdicts.ReferenceId, recorded, now)];

    private static DoctorCheck Check(string id, string verdictId, Reading<RecordedVerdicts> recorded, DateTimeOffset now)
    {
        if (recorded is not Reading<RecordedVerdicts>.Available { Value: var run })
        {
            return new(id, DoctorRun.Unknown, $"no full run has recorded it: {recorded.ReasonOrEmpty}");
        }

        var age = string.Create(CultureInfo.InvariantCulture, $" (full run {run.RunId.Text}, {(now - run.EvaluatedAt).TotalHours:0.0} h ago)");
        return run.Verdicts.FirstOrDefault(v => v.Id == verdictId) is { } verdict
            ? Of(id, verdict, age)
            : new(id, DoctorRun.Unknown, $"the newest full run recorded no {verdictId} verdict (a daemon older than the Windows Time guard){age}");
    }

    private static DoctorCheck Of(string id, Verdict verdict, string age) => verdict.Level switch
    {
        Level.Critical => new(id, DoctorRun.Problem, $"{verdict.Value}: {verdict.Reason}{age}"),
        Level.Warn => new(id, DoctorRun.Ok, $"warning: {verdict.Value}: {verdict.Reason}{age}"),
        Level.Ok => new(id, DoctorRun.Ok, $"{verdict.Value}: {verdict.Reason}{age}"),
        _ => new(id, DoctorRun.Unknown, $"{verdict.Reason}{age}"),
    };
}
