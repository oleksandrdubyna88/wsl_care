using WslCare.Core.Processes.Policy;

namespace WslCare.Core.Config;

/// <summary>
/// E7.S2b/S2c review C-H2: the two derived budgets of a run, from the configuration in force. A timer run's WORST CASE — every
/// command template of the product's catalogue once at its ceiling, each killed command's two drains, the daily folder walk and
/// the agent walk, a margin — is what <c>timer.runLimitMinutes</c> (wsl-care.service's <c>TimeoutStartSec</c>) must stay above; the
/// LONGEST STEP — the longest single command ceiling with its drains and a margin — is what <c>running.noProgressMinutes</c> must
/// stay above, since a command still inside its ceiling is progress.
/// </summary>
/// <remarks>Counted once per template: a removal of many batches, or a tool trim per tool, takes more — the backstop is a limit
/// on a run that hangs, not a plan of a busy one; the progress watchdog (<c>running.noProgressMinutes</c>) is what ends a hang
/// early.</remarks>
public static class RunBudget
{
    /// <summary>What a timer run keeps above its worst case.</summary>
    public static TimeSpan Margin => TimeSpan.FromMinutes(NumberRules.RunMarginMinutes);

    /// <summary>The keys the budgets read — a layer that sets one of them can break the rules that hold the budgets.</summary>
    public static IReadOnlyList<ConfigKey.IntKey> Keys { get; } =
        [.. ConfigKeys.All.OfType<ConfigKey.IntKey>().Where(k => k.Name.Contains("TimeoutSeconds", StringComparison.OrdinalIgnoreCase)),
            ConfigKeys.Commands.DrainGraceMilliseconds, ConfigKeys.Walk.MaxSeconds, ConfigKeys.Agents.WalkBudgetSeconds];

    public static TimeSpan TimerRunWorstCase(EffectiveConfig config)
    {
        using (Tuning.Use(config))
        {
            var drains = Tuning.Current.Milliseconds(ConfigKeys.Commands.DrainGraceMilliseconds) * 2;
            var commands = Templates().Aggregate(TimeSpan.Zero, (sum, t) => sum + t.Ceiling + drains);
            return commands + Tuning.Current.Seconds(ConfigKeys.Walk.MaxSeconds) + Tuning.Current.Seconds(ConfigKeys.Agents.WalkBudgetSeconds) + Margin;
        }
    }

    public static TimeSpan LongestStep(EffectiveConfig config)
    {
        using (Tuning.Use(config))
        {
            var drains = Tuning.Current.Milliseconds(ConfigKeys.Commands.DrainGraceMilliseconds) * 2;
            return Templates().Max(t => t.Ceiling) + drains + TimeSpan.FromSeconds(NumberRules.CeilingMarginSeconds);
        }
    }

    private static IEnumerable<CommandTemplate> Templates() => CommandCatalogue.Product.Templates.Distinct(ReferenceEqualityComparer.Instance).Cast<CommandTemplate>();
}
