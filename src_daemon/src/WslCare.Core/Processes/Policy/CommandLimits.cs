using WslCare.Core.Config;

namespace WslCare.Core.Processes.Policy;

/// <summary>A command template's limits (E7.S2c): configuration keys read at the moment a request is built, or fixed values.</summary>
public abstract record CommandLimits
{
    private CommandLimits()
    {
    }

    public abstract TimeSpan Ceiling { get; }

    public abstract int OutputCapChars { get; }

    /// <summary>Whether a timer run's worst case counts this ceiling (<see cref="RunBudget.TimerRunWorstCase"/>): a BUDGETED
    /// template takes the run limit's slack instead, and a button-only one is never part of a timer run (plan §15r D8).</summary>
    public virtual bool CountsInTimerRun => true;

    /// <summary>Read from the process's configuration (<see cref="Tuning.Current"/>) each time.</summary>
    public sealed record Keyed(ConfigKey.IntKey TimeoutSeconds, ConfigKey.IntKey OutputCapBytes) : CommandLimits
    {
        public override TimeSpan Ceiling => Tuning.Current.Seconds(TimeoutSeconds);

        public override int OutputCapChars => Tuning.Current.Int(OutputCapBytes);
    }

    /// <summary>The limits of a read command, built again each time (its ceiling and cap are keys read when it is built).</summary>
    public sealed record Of(Func<ToolCommand> Command) : CommandLimits
    {
        public override TimeSpan Ceiling => Command().Ceiling;

        public override int OutputCapChars => Command().OutputCapChars;
    }

    /// <summary>The archive run's child (plan §15r D8): its ceiling is chosen PER REQUEST by the action from the run limit's slack,
    /// at most <paramref name="BudgetMinutes"/> + <paramref name="GraceMinutes"/> — the most a request may ask, and what this
    /// reports; never a term of the timer run's worst case.</summary>
    public sealed record Budgeted(ConfigKey.IntKey BudgetMinutes, ConfigKey.IntKey GraceMinutes, ConfigKey.IntKey OutputCapBytes) : CommandLimits
    {
        public override TimeSpan Ceiling => TimeSpan.FromMinutes(Tuning.Current.Int(BudgetMinutes) + Tuning.Current.Int(GraceMinutes));

        public override int OutputCapChars => Tuning.Current.Int(OutputCapBytes);

        public override bool CountsInTimerRun => false;
    }

    /// <summary>A button-only action's child (A20, the restore): <paramref name="LimitMinutes"/> and the margin a ceiling keeps
    /// (the session in flight finishes); never part of a timer run.</summary>
    public sealed record ButtonOnly(ConfigKey.IntKey LimitMinutes, ConfigKey.IntKey OutputCapBytes) : CommandLimits
    {
        public override TimeSpan Ceiling => TimeSpan.FromMinutes(Tuning.Current.Int(LimitMinutes)) + TimeSpan.FromSeconds(NumberRules.CeilingMarginSeconds);

        public override int OutputCapChars => Tuning.Current.Int(OutputCapBytes);

        public override bool CountsInTimerRun => false;
    }

    /// <summary>Values fixed when the template was made.</summary>
    public sealed record Fixed(TimeSpan CeilingValue, int OutputCapValue) : CommandLimits
    {
        public override TimeSpan Ceiling => CeilingValue;

        public override int OutputCapChars => OutputCapValue;
    }
}
