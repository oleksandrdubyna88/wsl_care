namespace WslCare.Core.Config;

/// <summary>The <c>aptCache.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key.</summary>
public static partial class ConfigKeys
{
    public static class AptCache
    {
        /// <summary>A9 cleans the apt cache above this size. Default 200.</summary>
        public static readonly ConfigKey.IntKey TriggerMb = new("aptCache.triggerMb", 0, 100000) { Trust = KeyTrust.Higher };

        /// <summary>apt-get clean. Default 300.</summary>
        public static readonly ConfigKey.IntKey CleanTimeoutSeconds = new("aptCache.cleanTimeoutSeconds", 30, 300) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }
}
