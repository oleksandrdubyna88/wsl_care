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
            ConfigKeys.Commands.DrainGraceMilliseconds, ConfigKeys.Walk.MaxSeconds, ConfigKeys.Agents.WalkBudgetSeconds, ConfigKeys.Archive.ProgressSilenceSeconds];

    /// <summary>Every template once at its ceiling with its drains — but a BUDGETED or button-only one (plan §15r D8: the archive
    /// run takes the slack, the restore is never in a timer run) — the two walks, the margin.</summary>
    public static TimeSpan TimerRunWorstCase(EffectiveConfig config)
    {
        using (Tuning.Use(config))
        {
            var buttons = OnButtonsOnly();
            return Sum(Templates().Where(t => !buttons.Contains(t))) + Tuning.Current.Seconds(ConfigKeys.Walk.MaxSeconds) + Tuning.Current.Seconds(ConfigKeys.Agents.WalkBudgetSeconds) + Margin;
        }
    }

    /// <summary>The keys a watch run's worst case reads (plan E14 S2b).</summary>
    public static IReadOnlyList<ConfigKey.IntKey> WatchKeys { get; } =
        [ConfigKeys.McpServers.CpuWindowMilliseconds, ConfigKeys.McpServers.LogListMilliseconds, ConfigKeys.Processes.TermGraceSeconds];

    /// <summary>A watch run's WORST CASE (plan E14 S2b) — what <c>mcpWatchdog.runLimitMinutes</c> (wsl-care-watch.service's
    /// <c>TimeoutStartSec</c>) must stay above: the CPU window, the log listing of every server the watched list can hold (the catalogue's
    /// and the user's <c>mcpServers.programs</c> at their cap), A19's one shared signal grace, and a command's margin.</summary>
    public static TimeSpan WatchRunWorstCase(EffectiveConfig config)
    {
        using (Tuning.Use(config))
        {
            var servers = Mcp.McpServerCatalogue.Servers.Count + Mcp.McpUserPrograms.MaxMembers;
            return Tuning.Current.Milliseconds(ConfigKeys.McpServers.CpuWindowMilliseconds)
                + (Tuning.Current.Milliseconds(ConfigKeys.McpServers.LogListMilliseconds) * servers)
                + Tuning.Current.Seconds(ConfigKeys.Processes.TermGraceSeconds)
                + TimeSpan.FromSeconds(NumberRules.CeilingMarginSeconds);
        }
    }

    /// <summary>The worst case of <paramref name="actions"/> alone — their templates once each at its ceiling with its drains, the
    /// budgeted and button-only ones left out: what A13 keeps free for the actions after it (plan §15r D8).</summary>
    public static TimeSpan WorstCaseOf(IEnumerable<Actions.ICleanupAction> actions, EffectiveConfig config)
    {
        using (Tuning.Use(config))
        {
            return Sum(actions.Where(a => !a.Id.ButtonOnly).SelectMany(a => a.Commands).Distinct(ReferenceEqualityComparer.Instance).Cast<CommandTemplate>());
        }
    }

    /// <summary>The longest single step: a template's ceiling — a STREAMED one's line silence (<c>archive.progressSilenceSeconds</c>),
    /// since each line it prints is progress (plan §15r D8) — with its drains and the margin a ceiling keeps.</summary>
    public static TimeSpan LongestStep(EffectiveConfig config)
    {
        using (Tuning.Use(config))
        {
            var drains = Tuning.Current.Milliseconds(ConfigKeys.Commands.DrainGraceMilliseconds) * 2;
            return Templates().Max(Step) + drains + TimeSpan.FromSeconds(NumberRules.CeilingMarginSeconds);
        }
    }

    private static TimeSpan Step(CommandTemplate template) =>
        template.Streamed ? Tuning.Current.Seconds(ConfigKeys.Archive.ProgressSilenceSeconds) : template.Ceiling;

    private static TimeSpan Sum(IEnumerable<CommandTemplate> templates)
    {
        var drains = Tuning.Current.Milliseconds(ConfigKeys.Commands.DrainGraceMilliseconds) * 2;
        return templates.Where(t => t.Limits.CountsInTimerRun).Aggregate(TimeSpan.Zero, (sum, t) => sum + t.Ceiling + drains);
    }

    /// <summary>The templates only button-only actions declare (A20's list): never part of a timer run (plan §15r D8).</summary>
    private static IReadOnlySet<CommandTemplate> OnButtonsOnly()
    {
        var actions = Actions.ActionRegistry.Product.Actions;
        var timer = actions.Where(a => !a.Id.ButtonOnly).SelectMany(a => a.Commands).ToHashSet(ReferenceEqualityComparer.Instance);
        return actions.Where(a => a.Id.ButtonOnly).SelectMany(a => a.Commands).Where(c => !timer.Contains(c)).ToHashSet();
    }

    private static IEnumerable<CommandTemplate> Templates() => CommandCatalogue.Product.Templates.Distinct(ReferenceEqualityComparer.Instance).Cast<CommandTemplate>();
}
