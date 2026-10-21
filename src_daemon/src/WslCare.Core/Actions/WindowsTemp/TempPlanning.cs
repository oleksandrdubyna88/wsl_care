using WslCare.Core.Files;

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
        TempRoots.Resolve(context.Paths) switch
        {
            TempRootResult.Resolved resolved => PlanRoot(context, settings, resolved.Root, token),
            TempRootResult.Refused refused => new Refused(refused.Reason),
            _ => throw new System.Diagnostics.UnreachableException("TempRootResult is a closed set"),
        };

    private static Planning PlanRoot(ActionContext context, TempSettings settings, string root, CancellationToken token)
    {
        var listBudget = new TempBudget(settings.ScanMaxNodes, settings.ScanMaxTime, context.Clock);
        return context.Files.ListEntries(root, new ListingBounds(settings.MaxTopLevelEntries, listBudget.OutOfTime, token)) switch
        {
            EntryListing.Listed { Complete: true } listed => new Planned(TempPlanner.Plan(context, settings, root, listed.Entries, listBudget, token)),
            EntryListing.Listed cut => new Refused($"{root} holds more than {settings.MaxTopLevelEntries} top-level entries or could not be listed in time ({cut.Note}); raise temp.maxTopLevelEntries or temp.scanMaxSeconds - a cut listing has no stable order for the cursor"),
            EntryListing.Unreadable unreadable => new Unreadable(unreadable.Reason),
            _ => throw new System.Diagnostics.UnreachableException("EntryListing is a closed set"),
        };
    }
}
