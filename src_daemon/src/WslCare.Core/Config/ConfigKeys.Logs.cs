namespace WslCare.Core.Config;

/// <summary>The <c>logs.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key.</summary>
public static partial class ConfigKeys
{
    public static partial class Logs
    {
        /// <summary>Run details one logs answer opens. Default 50.</summary>
        public static readonly ConfigKey.IntKey MaxDetailsRead = new("logs.maxDetailsRead", 1, 50) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The longest logs / runs range (at least runs.historyRetentionDays). Default 366.</summary>
        public static readonly ConfigKey.IntKey MaxRangeDays = new("logs.maxRangeDays", 90, 366) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }
}
