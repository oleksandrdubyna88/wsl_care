namespace WslCare.Scenarios;

/// <summary>
/// The collection of flows that measure wall-clock time: xUnit runs it with no other test in parallel, so a
/// budget is held by the verb under test and not by whatever else the suite is starting at that moment.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WallClock
{
    public const string Name = "wall-clock budgets";
}
