using WslCare.Core.Files;
using WslCare.Core.Records;

namespace WslCare.Core.Actions.WindowsTemp;

/// <summary>What planning a run of A23 produced — a closed set (WR-T2: the one planning both the engine's preview and
/// <c>tasks preview cleanup</c> read).</summary>
internal abstract record Planning
{
    private Planning()
    {
    }

    /// <summary>The top-level folder was listed and judged: the plan.</summary>
    public sealed record Planned(TempPlan Plan) : Planning;

    /// <summary>Nothing is planned, and why: the root was refused (it is not the user's own temp folder) or its listing was cut.</summary>
    public sealed record Refused(string Reason) : Planning;

    /// <summary>The folder could not be read at all.</summary>
    public sealed record Unreadable(string Reason) : Planning;

    /// <summary>
    /// Resolves the root, lists its top level under <c>temp.maxTopLevelEntries</c> and the run's ONE budget, and plans — the shared half of
    /// the engine's preview and of the cleanup task's preview verb. ONE call site of the root listing (WR-T2, reuse-first move 2): there
    /// is no second selection code. Reads only: nothing is written, nothing deleted.
    /// </summary>
    public static Planning Of(ActionContext context, TempSettings settings, CancellationToken token) =>
        List(context, settings, token) switch
        {
            Listing.Ready ready => new Planned(TempPlanner.Plan(context, settings, ready.Root, ready.Entries, ready.Budget, token)),
            Listing.Stopped stopped => stopped.Outcome,
            _ => throw new System.Diagnostics.UnreachableException("Listing is a closed set"),
        };

    /// <summary>
    /// The preview's two plans over ONE listing and ONE budget (code round 4663beda, findings 1 and 8): first the plan the scheduled task will
    /// make (the <c>Timer</c> trigger - it has the budget and the priority), then the plan a person's trigger makes, which also scans the names
    /// the allow-list does not cover and gets only what the first left of the same <see cref="TempBudget"/>. Nothing here lists twice or
    /// spends twice; the second is <c>null</c> when the first was not a plan.
    /// </summary>
    public static (Planning Task, TempPlan? Person) OfTaskThenPerson(ActionContext context, TempSettings settings, CancellationToken token) =>
        List(context, settings, token) switch
        {
            Listing.Ready ready => (new Planned(PlanAs(RunTrigger.Timer, context, settings, ready, token)), PlanAs(RunTrigger.Cli, context, settings, ready, token)),
            Listing.Stopped stopped => (stopped.Outcome, null),
            _ => throw new System.Diagnostics.UnreachableException("Listing is a closed set"),
        };

    private static TempPlan PlanAs(RunTrigger trigger, ActionContext context, TempSettings settings, Listing.Ready ready, CancellationToken token) =>
        TempPlanner.Plan(context with { Trigger = trigger }, settings, ready.Root, ready.Entries, ready.Budget, token);

    private static Listing List(ActionContext context, TempSettings settings, CancellationToken token) =>
        TempRoots.Resolve(context.Paths) switch
        {
            TempRootResult.Resolved resolved => ListRoot(context, settings, resolved.Root, token),
            TempRootResult.Refused refused => new Listing.Stopped(new Refused(refused.Reason)),
            _ => throw new System.Diagnostics.UnreachableException("TempRootResult is a closed set"),
        };

    private static Listing ListRoot(ActionContext context, TempSettings settings, string root, CancellationToken token)
    {
        var budget = new TempBudget(settings.ScanMaxNodes, settings.ScanMaxTime, context.Clock);
        return context.Files.ListEntries(root, new ListingBounds(settings.MaxTopLevelEntries, budget.OutOfTime, token)) switch
        {
            EntryListing.Listed { Complete: true } listed => new Listing.Ready(root, listed.Entries, budget),
            EntryListing.Listed cut => new Listing.Stopped(new Refused($"{root} holds more than {settings.MaxTopLevelEntries} top-level entries or could not be listed in time ({cut.Note}); raise temp.maxTopLevelEntries or temp.scanMaxSeconds - a cut listing has no stable order for the cursor")),
            EntryListing.Unreadable unreadable => new Listing.Stopped(new Unreadable(unreadable.Reason)),
            _ => throw new System.Diagnostics.UnreachableException("EntryListing is a closed set"),
        };
    }

    /// <summary>The top-level listing: the names and the run's ONE budget (the time the listing took is already spent from it), or why there is no plan.</summary>
    private abstract record Listing
    {
        public sealed record Ready(string Root, IReadOnlyList<FileEntry> Entries, TempBudget Budget) : Listing;

        public sealed record Stopped(Planning Outcome) : Listing;
    }
}
