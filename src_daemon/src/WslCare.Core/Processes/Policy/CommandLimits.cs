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

    /// <summary>Values fixed when the template was made.</summary>
    public sealed record Fixed(TimeSpan CeilingValue, int OutputCapValue) : CommandLimits
    {
        public override TimeSpan Ceiling => CeilingValue;

        public override int OutputCapChars => OutputCapValue;
    }
}
