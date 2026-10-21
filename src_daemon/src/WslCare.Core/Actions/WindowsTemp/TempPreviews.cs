using System.Globalization;

using WslCare.Core.Actions.Engine;
using WslCare.Core.Config;
using WslCare.Core.Records;

namespace WslCare.Core.Actions.WindowsTemp;

/// <summary>
/// Builds <see cref="TempPreviewReport"/> from the SAME plan the engine's preview reads (<see cref="Planning"/>) - one listing, one
/// planner, two shapes (WR-T2, reuse-first move 2). Reads only: the plan reads the cursor and the folder and writes nothing, and this
/// builder only shapes what it found. The exact paths are BOUNDED by <c>preview.maxItems</c> (the coai plan round 4663beda, finding 3):
/// at most that many groups per verdict and that many paths per group, the remainders counted.
/// </summary>
internal static class TempPreviews
{
    public const string Basis = "logical bytes of the files in the selected trees (an estimate; never disk space freed)";

    private const string Eligible = "eligible";

    private const string WouldBeEligible = "wouldBeEligible";

    public static TempPreviewReport Report(ActionContext context, TempSettings settings, DryRunPeek dry, CancellationToken token)
    {
        var limit = context.Config.Int(ConfigKeys.Preview.MaxItems);

        // The counts, the groups and the eligible verdict are the TASK's: planned as the timer plans (the names the allow-list does not cover
        // cost one node each and are not scanned), so the shared scan budget is spent as the task will spend it (the Fable own review of
        // WR-T2, major 1). "Would be eligible if allowed" needs those names scanned, so it comes from a second, person-trigger plan.
        return Planning.Of(context with { Trigger = RunTrigger.Timer }, settings, token) switch
        {
            Planning.Planned planned => FromPlan(context, settings, dry, planned.Plan, Uncovered(context, settings, token), limit),
            Planning.Refused refused => Nothing(context, settings, dry, string.Empty, refused.Reason, limit),
            Planning.Unreadable unreadable => Nothing(context, settings, dry, string.Empty, unreadable.Reason, limit),
            _ => throw new System.Diagnostics.UnreachableException("Planning is a closed set"),
        };
    }

    /// <summary>The plan a PERSON's trigger makes, which scans the names the allow-list does not cover; <c>null</c>-free: an empty plan when it
    /// cannot be made (the same root was just planned, so this is not expected).</summary>
    private static TempPlan? Uncovered(ActionContext context, TempSettings settings, CancellationToken token) =>
        Planning.Of(context with { Trigger = RunTrigger.Cli }, settings, token) is Planning.Planned planned ? planned.Plan : null;

    private static TempPreviewReport FromPlan(ActionContext context, TempSettings settings, DryRunPeek dry, TempPlan plan, TempPlan? uncovered, int limit)
    {
        var eligible = Groups(plan, TempVerdict.Eligible, Eligible, limit);
        (IReadOnlyList<TempPreviewGroup> Shown, int NotShown) would = uncovered is null ? ([], 0) : Groups(uncovered, TempVerdict.WouldBeEligible, WouldBeEligible, limit);
        var counts = new TempPreviewCounts(
            plan.Count(TempVerdict.Eligible), uncovered?.Count(TempVerdict.WouldBeEligible) ?? 0, plan.Count(TempVerdict.NotAllowed), plan.Count(TempVerdict.Young),
            plan.Count(TempVerdict.Excluded), plan.Count(TempVerdict.Unknown), plan.Count(TempVerdict.Changed), plan.Deferred, plan.Entries.Count, plan.Total);
        return Nothing(context, settings, dry, plan.Root, string.Empty, limit, plan.Total) with
        {
            Counts = counts,
            EligibleBytes = plan.Eligible.Sum(e => e.Judgement.Bytes),
            ExcludedByReason = [.. plan.Entries.Where(e => e.Judgement.Verdict == TempVerdict.Excluded).GroupBy(e => e.Judgement.Reason).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => new TempExcludedReason(g.Key, g.Count()))],
            Groups = [.. eligible.Shown, .. would.Shown],
            GroupsNotShown = new TempGroupsNotShown(eligible.NotShown, would.NotShown),
            WouldBeEligibleLimited = uncovered is { Deferred: > 0 },
        };
    }

    /// <summary>The groups of one verdict: the largest <paramref name="limit"/> of them, each with at most <paramref name="limit"/> paths.</summary>
    private static (IReadOnlyList<TempPreviewGroup> Shown, int NotShown) Groups(TempPlan plan, TempVerdict verdict, string label, int limit)
    {
        var groups = plan.Entries.Where(e => e.Judgement.Verdict == verdict).GroupBy(e => e.Judgement.Group)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        var shown = groups.Take(limit).Select(g => new TempPreviewGroup(label, g.Key, g.Count(), g.Sum(e => e.Judgement.Bytes), [.. g.Take(limit).Select(e => e.Candidate.Path)], Math.Max(0, g.Count() - limit))).ToList();
        return (shown, groups.Count - shown.Count);
    }

    /// <summary>The answer when nothing was planned: the refusal (or empty), zero counts - and the frame every answer shares.</summary>
    private static TempPreviewReport Nothing(ActionContext context, TempSettings settings, DryRunPeek dry, string root, string refusal, int limit, int total = 0)
    {
        var cursor = TempCursors.Read(context.Paths, context.Files);
        return new TempPreviewReport(
            Core.SchemaVersion.Current,
            string.Empty,
            "cleanup",
            WindowsTempAction.StampName,
            root,
            refusal,
            ObserveOnly: false,
            [],
            (int)settings.OlderThan.TotalDays,
            (context.Clock.GetUtcNow() - settings.OlderThan).UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            settings.AllowGlobs,
            settings.ScanMaxNodes,
            (int)settings.ScanMaxTime.TotalSeconds,
            settings.MaxItemsPerRun,
            limit,
            dry,
            new TempPreviewPass(cursor.PassStarted == DateTimeOffset.MinValue ? string.Empty : cursor.PassStarted.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), cursor.PassDecided, total, cursor.PassesCompleted, cursor.Last, cursor.InProgress.Name),
            new TempPreviewCounts(0, 0, 0, 0, 0, 0, 0, 0, 0, total),
            0,
            Basis,
            [],
            [],
            new TempGroupsNotShown(0, 0),
            WouldBeEligibleLimited: false);
    }
}
