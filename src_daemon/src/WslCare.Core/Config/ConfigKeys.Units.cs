namespace WslCare.Core.Config;

/// <summary>The <c>units.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key.</summary>
public static partial class ConfigKeys
{
    public static class Units
    {
        /// <summary>The services drop-in Nice. Default 19.</summary>
        public static readonly ConfigKey.IntKey Nice = new("units.nice", 0, 19) { Trust = new(SafeDirection.Higher, MachineOnly: true) };

        /// <summary>The services drop-in MemoryMax. Default 1024.</summary>
        public static readonly ConfigKey.IntKey MemoryMaxMb = new("units.memoryMaxMb", 256, 8192) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The services drop-in TimeoutStopSec. Default 90.</summary>
        public static readonly ConfigKey.IntKey StopTimeoutSeconds = new("units.stopTimeoutSeconds", 30, 300) { Trust = new(SafeDirection.None, MachineOnly: true) };

        /// <summary>The follower service drop-in RestartSec. Default 30.</summary>
        public static readonly ConfigKey.IntKey EventsRestartSeconds = new("units.eventsRestartSeconds", 5, 600) { Trust = new(SafeDirection.Higher, MachineOnly: true) };
    }
}
