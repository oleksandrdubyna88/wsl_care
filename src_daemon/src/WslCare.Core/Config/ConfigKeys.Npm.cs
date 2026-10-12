namespace WslCare.Core.Config;

/// <summary>The <c>npm.*</c> keys, in their own file: one file per key group, never one group in two files (the doctrine caps
/// a file at 800 lines). The older keys come first; the E7.S2c number keys after them join <c>NumberKeys()</c> like every
/// other number key.</summary>
public static partial class ConfigKeys
{
    public static class Npm
    {
        public static readonly ConfigKey.IntKey MaxCacheGb = new("npm.maxCacheGb", 0, GbCeiling) { Trust = KeyTrust.Higher };

        /// <summary>npm cache clean. Default 600.</summary>
        public static readonly ConfigKey.IntKey CleanTimeoutSeconds = new("npm.cleanTimeoutSeconds", 60, 600) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }
}
