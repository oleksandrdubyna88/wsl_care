using WslCare.Core.Actions.Engine;

namespace WslCare.Core.Actions.WindowsTemp;

/// <summary>Where the cleanup stands in its pass over the temp folder's names — the cursor as it is NOW, on disk.</summary>
/// <param name="Started">When the current pass began (UTC, ISO 8601); empty before the first run.</param>
/// <param name="Decided">How many names the current pass has decided.</param>
/// <param name="Total">How many top-level names the folder holds now.</param>
/// <param name="PassesCompleted">How many passes wrapped round.</param>
/// <param name="Last">The last name decided: the next run starts after it.</param>
/// <param name="InProgress">The candidate an interrupted run left half deleted (the next run takes it first); empty when none.</param>
public sealed record TempPreviewPass(string Started, int Decided, int Total, int PassesCompleted, string Last, string InProgress);

/// <summary>The counts of a preview — the same figures <c>act A23 --preview</c> carries in its <c>facts</c>.</summary>
public sealed record TempPreviewCounts(long Eligible, long WouldBeEligible, long NotAllowed, long Young, long Excluded, long Unknown, long Changed, long Deferred, long Decided, long Total);

/// <summary>Entries never deleted, by the reason: <c>claude</c>, another owner, the test-leftover sweep's, a git working folder.</summary>
public sealed record TempExcludedReason(string Reason, int Count);

/// <summary>One group of the preview: the entries one allow-list pattern selects (<c>eligible</c>) or that would be selected if the pattern
/// were allowed (<c>wouldBeEligible</c>). BOUNDED: at most <c>preview.maxItems</c> exact paths; <paramref name="More"/> counts the rest.</summary>
/// <param name="Verdict"><c>eligible</c> or <c>wouldBeEligible</c>.</param>
/// <param name="Pattern">The allow-list glob that matched, or the derived group of a name no glob covers.</param>
/// <param name="Count">All the entries of the group.</param>
/// <param name="Bytes">Their logical bytes (an estimate; never disk space freed).</param>
/// <param name="Paths">The first paths, absolute.</param>
/// <param name="More">How many entries are not listed in <paramref name="Paths"/>.</param>
public sealed record TempPreviewGroup(string Verdict, string Pattern, int Count, long Bytes, IReadOnlyList<string> Paths, int More);

/// <summary>How many groups of each verdict the answer leaves out (past the <c>preview.maxItems</c> cap).</summary>
public sealed record TempGroupsNotShown(int Eligible, int WouldBeEligible);

/// <summary>
/// <c>tasks preview cleanup --json</c> (WR-T2 of PLAN_windows_runner.md §3.12): what the Windows <c>%TEMP%</c> cleanup would do now —
/// read-only, bounded, with the numbers in force and A23's own dry-run state. The contract the panel (WR-T3) reads; additive growth, as
/// the render's. A refused root answers with <paramref name="Refusal"/> and zero counts (exit 0, as <c>act --preview</c>).
/// </summary>
public sealed record TempPreviewReport(
    int SchemaVersion,
    string ProductVersion,
    string Task,
    string Action,
    string Root,
    string Refusal,
    bool ObserveOnly,
    IReadOnlyList<string> ConfigurationErrors,
    int OlderThanDays,
    string CutoffUtc,
    IReadOnlyList<string> AllowGlobs,
    int ScanMaxNodes,
    int ScanMaxSeconds,
    int MaxItemsPerRun,
    int MaxPathsPerGroup,
    DryRunPeek Dry,
    TempPreviewPass Pass,
    TempPreviewCounts Counts,
    long EligibleBytes,
    string Basis,
    IReadOnlyList<TempExcludedReason> ExcludedByReason,
    IReadOnlyList<TempPreviewGroup> Groups,
    TempGroupsNotShown GroupsNotShown,
    bool WouldBeEligibleLimited);

