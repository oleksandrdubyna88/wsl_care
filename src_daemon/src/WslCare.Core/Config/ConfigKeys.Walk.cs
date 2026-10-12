namespace WslCare.Core.Config;

/// <summary>The <c>walk.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key.</summary>
public static partial class ConfigKeys
{
    public static class Walk
    {
        /// <summary>The folders are walked again when the newest walk is older than this. Default 20.</summary>
        public static readonly ConfigKey.IntKey IntervalHours = new("walk.intervalHours", 4, 168) { Trust = KeyTrust.Higher };

        /// <summary>How many folders a walk left out are named; past it the answer says how many more. Default 20.</summary>
        public static readonly ConfigKey.IntKey MaxExclusionsNamed = new("walk.maxExclusionsNamed", 0, 100) { Trust = KeyTrust.Display };

        /// <summary>Entries one folder walk may see. Default 2000000.</summary>
        public static readonly ConfigKey.IntKey MaxEntries = new("walk.maxEntries", 1000, 2000000) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Time one folder walk may take. Default 120.</summary>
        public static readonly ConfigKey.IntKey MaxSeconds = new("walk.maxSeconds", 5, 120) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }
}
