namespace WslCare.Core.Tests;

/// <summary>
/// The tests that hold a WALL-CLOCK budget against real child processes (the runner's own tests): xUnit runs this
/// collection with no other test in parallel, so a budget is spent by the child under test and not by the suite's other
/// 600 tests starting at the same moment. Found 2026-10-03 hunting a flaky Core run under load: the shell loops of
/// <c>ProcessCommandRunnerTests</c> outran 45 s / 60 s / 6 s budgets on a loaded machine. Same shape as the scenarios'
/// collection of the same name.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WallClock
{
    public const string Name = "wall-clock budgets";
}
