namespace WslCare.Core.Config;

/// <summary>The <c>patterns.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key.</summary>
public static partial class ConfigKeys
{
    public static partial class Patterns
    {
        /// <summary>The match ceiling of every fixed pattern (a ReDoS guard). Default 250.</summary>
        public static readonly ConfigKey.IntKey MatchTimeoutMilliseconds = new("patterns.matchTimeoutMilliseconds", 50, 1000) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }
}
