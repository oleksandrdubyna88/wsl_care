using System.Text;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Archive;
using WslCare.Core.Config;
using WslCare.Core.Files;

namespace WslCare.Core.Tests.Config;

/// <summary>
/// Plan §15r D8, E9.S4 — the archive in the run's two derived budgets: the run child is BUDGETED (it takes the run limit's slack)
/// and the restore a button only, so neither is a term of a timer run's worst case; both are STREAMED, so the longest step counts
/// their line silence, not their ceiling — a long archive is progress, and <c>timer.runLimitMinutes</c> stays 240.
/// </summary>
public sealed class RunBudgetArchiveTests
{
    private static EffectiveConfig Machine(string json) =>
        ConfigLoader.Load([
            (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults())),
            (new ConfigLayerFile(ConfigLayer.Machine, "/etc/wsl-care/config.json"), new FileReadResult.Content(Encoding.UTF8.GetBytes(json))),
        ]).Config;

    private static readonly EffectiveConfig Defaults = Machine("{}");

    [Fact]
    public void The_budgeted_run_and_the_button_only_restore_are_not_terms_of_a_timer_runs_worst_case()
    {
        var wide = Machine("""{ "archive": { "runBudgetMinutes": 55, "finishGraceMinutes": 30, "restoreLimitMinutes": 59 } }""");

        RunBudget.TimerRunWorstCase(wide).Should().Be(RunBudget.TimerRunWorstCase(Defaults));
        RunBudget.TimerRunWorstCase(Defaults).Should().BeLessThan(TimeSpan.FromMinutes(Defaults.Int(ConfigKeys.Timer.RunLimitMinutes)), "timer.runLimitMinutes stays 240 (D8)");
    }

    [Fact]
    public void A_streamed_templates_step_is_its_line_silence_not_its_ceiling()
    {
        var longRestore = Machine("""{ "archive": { "restoreLimitMinutes": 59, "runBudgetMinutes": 55, "finishGraceMinutes": 30 } }""");
        var slowLines = Machine("""{ "archive": { "progressSilenceSeconds": 600 }, "running": { "noProgressMinutes": 30 } }""");

        RunBudget.LongestStep(longRestore).Should().Be(RunBudget.LongestStep(Defaults), "a streamed child's ceiling is not one step");
        RunBudget.LongestStep(slowLines).Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(600), "its silence is");
        ArchiveChildren.Run.Streamed.Should().BeTrue();
        ArchiveChildren.Restore.Streamed.Should().BeTrue();
    }

    /// <summary>What A13 keeps free for the actions behind it: their templates at their worst — a button-only action's none.</summary>
    [Fact]
    public void The_worst_case_of_the_actions_behind_A13_counts_only_what_a_timer_run_starts()
    {
        var behind = ActionRegistry.Product.InExecutionOrder([.. ActionId.ExecutionOrder.SkipWhile(id => id.Text != "A13").Skip(1)]);

        behind.Select(a => a.Id.Text).Should().Equal("A20", "A1", "A2");
        RunBudget.WorstCaseOf([new RestoreAction()], Defaults).Should().Be(TimeSpan.Zero, "A20 is never in a timer run");
        RunBudget.WorstCaseOf(behind, Defaults).Should().Be(RunBudget.WorstCaseOf([.. behind.Where(a => a.Id.Text != "A20")], Defaults));
    }
}
