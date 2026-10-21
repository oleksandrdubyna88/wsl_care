using System.Globalization;

using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Processes.Policy;

namespace WslCare.Core.Actions.WindowsTemp;

/// <summary>
/// A23, the Windows <c>%TEMP%</c> cleanup (PLAN_windows_runner.md §3.12) — the engine's first Windows-side action. It deletes the
/// current user's own top-level entries of <c>%TEMP%</c> that match the allow-list and whose newest descendant is older than the age
/// limit, never <c>claude</c>, never an entry another account owns, never through (or out of) a reparse point, never a git working
/// folder. The preview shows MORE than the run deletes (what the allow-list does not cover but would qualify); every run is bounded
/// (entries, seconds, items) and resumes after the last decided candidate; a candidate is checkpointed before its first delete and
/// re-validated just before it.
/// </summary>
public sealed class WindowsTempAction(Func<ActionContext, TempSettings> settingsOf) : ICleanupAction
{
    public WindowsTempAction() : this(static c => TempSettings.From(c.Config))
    {
    }

    /// <summary>A23's id, spelt here and nowhere else: the action's own <see cref="Id"/>, its dry-week <see cref="StampName"/>, the task worker's argument and the task's stamp all read this one constant.</summary>
    public const string IdText = "A23";

    public ActionId Id { get; } = ActionId.Find(IdText)!;

    public string Summary => "clean the Windows TEMP folder of the user's own old scratch entries";

    public CommandScope Scope => CommandScope.Machine;

    public IdleRule Idle => IdleRule.Never;

    /// <summary>Not part of a full check's timer pass: the cleanup has its own task (WR-T2).</summary>
    public bool InFullCheck => false;

    /// <summary>A23 keeps its own first dry week (<c>first-timer-run-A23.json</c>): the common stamp is started by the Windows watch and the
    /// archive body, so it would open deletion on the first A23 run without any dry week of its own.</summary>
    public string DryWindowStamp => StampName;

    /// <summary>The name of A23's own dry-run week stamp: <c>first-timer-run-A23.json</c>. Equal to the action id (<see cref="IdText"/>).</summary>
    public const string StampName = IdText;

    public IReadOnlyList<HostSide> Sides { get; } = [HostSide.Windows];

    public IReadOnlyList<CommandTemplate> Commands { get; } = [];

    public Task<ActionPreview> PreviewAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken)
    {
        var settings = settingsOf(context);
        return Task.FromResult(Planning.Of(context, settings, cancellationToken) switch
        {
            Planning.Planned planned => Previewed(planned.Plan, settings),
            Planning.Refused refused => Refused(settings, refused.Reason),
            Planning.Unreadable unreadable => ActionPreview.Unavailable(WhatIs(settings), unreadable.Reason),
            _ => throw new System.Diagnostics.UnreachableException("Planning is a closed set"),
        });
    }

    public TriggerDecision Trigger(ActionPreview preview, EffectiveConfig config) =>
        preview.Count > 0 ? new TriggerDecision(true, $"{preview.Count} top-level entries are old, owned and allowed") : new TriggerDecision(false, "nothing in the temp folder is old, owned and allowed");

    public Task<ActionRun> RunAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken) =>
        Task.FromResult(new TempRun(context, settingsOf(context), preview, cancellationToken).Execute());

    private static string WhatIs(TempSettings settings) =>
        string.Create(CultureInfo.InvariantCulture, $"delete the user's own top-level entries of the temp folder that match {string.Join(", ", settings.AllowGlobs)} and whose newest descendant is older than {settings.OlderThan.TotalDays:0} days");

    private static ActionPreview Refused(TempSettings settings, string reason) =>
        ActionPreview.Of(WhatIs(settings), 0, 0, "nothing is selected", new Dictionary<string, long>(), reason, []);

    private static ActionPreview Previewed(TempPlan plan, TempSettings settings)
    {
        var eligible = plan.Eligible;
        var targets = eligible.Select(e => new ActionItem(e.Candidate.Kind == EntryKind.Directory ? "folder" : "file", e.Candidate.Path, e.Judgement.Bytes, e.Resumes ? $"resumes an interrupted deletion ({e.Judgement.Group})" : e.Judgement.Group) { Key = e.Candidate.Name }).ToList();
        return ActionPreview.Of(What(plan, settings), targets.Count, eligible.Sum(e => e.Judgement.Bytes), "logical bytes of the files in the selected trees (an estimate; never disk space freed)", Facts(plan), string.Empty, targets) with
        {
            Carry = TempCarry.Of(plan),
        };
    }

    private static string What(TempPlan plan, TempSettings settings)
    {
        var never = plan.Entries.Where(e => e.Judgement.Verdict == TempVerdict.Excluded).GroupBy(e => e.Judgement.Reason).Select(g => $"{g.Count()} {g.Key}");
        var started = plan.Next.PassStarted.UtcDateTime.ToString("yyyy-MM-dd HH:mm'Z'", CultureInfo.InvariantCulture);
        return string.Create(CultureInfo.InvariantCulture, $"{WhatIs(settings)}; in {plan.Root}; pass started {started}, {plan.Next.PassDecided} of {plan.Total} names decided; never deleted: {(never.Any() ? string.Join(", ", never) : "none met")}");
    }

    private static Dictionary<string, long> Facts(TempPlan plan)
    {
        var facts = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            [TempFacts.Eligible] = plan.Count(TempVerdict.Eligible),
            [TempFacts.WouldBeEligible] = plan.Count(TempVerdict.WouldBeEligible),
            [TempFacts.NotAllowed] = plan.Count(TempVerdict.NotAllowed),
            [TempFacts.Young] = plan.Count(TempVerdict.Young),
            [TempFacts.Excluded] = plan.Count(TempVerdict.Excluded),
            [TempFacts.Unknown] = plan.Count(TempVerdict.Unknown),
            [TempFacts.Changed] = plan.Count(TempVerdict.Changed),
            [TempFacts.Deferred] = plan.Deferred,
            [TempFacts.Decided] = plan.Entries.Count,
            [TempFacts.Total] = plan.Total,
        };
        Groups(plan, TempVerdict.Eligible, "eligible", facts);
        Groups(plan, TempVerdict.WouldBeEligible, "wouldBeEligible", facts);
        return facts;
    }

    /// <summary>The count per pattern of one verdict, the largest <see cref="ActionPreview.MaxItems"/> groups.</summary>
    private static void Groups(TempPlan plan, TempVerdict verdict, string label, Dictionary<string, long> facts)
    {
        foreach (var group in plan.Entries.Where(e => e.Judgement.Verdict == verdict).GroupBy(e => e.Judgement.Group).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(ActionPreview.MaxItems))
        {
            facts[$"{TempFacts.GroupPrefix}{group.Key}:{label}"] = group.Count();
        }
    }
}
