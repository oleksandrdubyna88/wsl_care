namespace WslCare.Core.Config;

/// <summary>The <c>running.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key.</summary>
public static partial class ConfigKeys
{
    public static partial class Running
    {
        /// <summary>running.json heartbeat period (wedged-after must be at least 3 times this). Default 5.</summary>
        public static readonly ConfigKey.IntKey HeartbeatSeconds = new("running.heartbeatSeconds", 1, 60) { Trust = new(SafeDirection.None, MachineOnly: true) };

        /// <summary>A heartbeat older than this means wedged. Default 30.</summary>
        public static readonly ConfigKey.IntKey WedgedAfterSeconds = new("running.wedgedAfterSeconds", 10, 600) { Trust = new(SafeDirection.Higher, MachineOnly: true) };

        /// <summary>Reads of running.json before a verdict. Default 3.</summary>
        public static readonly ConfigKey.IntKey ReadRetries = new("running.readRetries", 1, 10) { Trust = new(SafeDirection.None, MachineOnly: true) };

        /// <summary>The pause between those reads. Default 100.</summary>
        public static readonly ConfigKey.IntKey ReadRetryMilliseconds = new("running.readRetryMilliseconds", 10, 1000) { Trust = new(SafeDirection.None, MachineOnly: true) };

        /// <summary>A run whose heartbeat is fresh but that made no step of progress (a command started or ended, a folder walked,
        /// an action begun) for this long reads WEDGED, so act --stop can end it (E7.S2b/S2c review C-H2; at least the longest single
        /// command ceiling plus its drain and a margin). Default 20.</summary>
        public static readonly ConfigKey.IntKey NoProgressMinutes = new("running.noProgressMinutes", 5, 1440) { Trust = new(SafeDirection.Higher, MachineOnly: true) };
    }
}
