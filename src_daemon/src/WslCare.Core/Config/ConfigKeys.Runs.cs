namespace WslCare.Core.Config;

/// <summary>The <c>runs.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key.</summary>
public static partial class ConfigKeys
{
    public static partial class Runs
    {
        /// <summary>How long a stop marker is kept. Default 24.</summary>
        public static readonly ConfigKey.IntKey StopMarkerRetentionHours = new("runs.stopMarkerRetentionHours", 1, 168) { Trust = KeyTrust.Higher };

        /// <summary>History lines and run details are kept this long. Default 90.</summary>
        public static readonly ConfigKey.IntKey HistoryRetentionDays = new("runs.historyRetentionDays", 7, 366) { Trust = new(SafeDirection.Higher, TightenOnlyForRoot: true) };
    }
}
