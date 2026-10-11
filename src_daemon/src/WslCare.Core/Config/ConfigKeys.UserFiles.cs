namespace WslCare.Core.Config;

/// <summary>The <c>userFiles.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key.</summary>
public static partial class ConfigKeys
{
    public static class UserFiles
    {
        /// <summary>Bytes of a user's small file read by root (nvm's alias). Default 4096.</summary>
        public static readonly ConfigKey.IntKey MaxSmallFileBytes = new("userFiles.maxSmallFileBytes", 256, 4096) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Bytes of a user's JSON file read by root (browsers.json, .obsolete). Default 1048576.</summary>
        public static readonly ConfigKey.IntKey MaxJsonBytes = new("userFiles.maxJsonBytes", 65536, 1048576) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Bytes of a Playwright link file read by root. Default 65536.</summary>
        public static readonly ConfigKey.IntKey MaxLinkBytes = new("userFiles.maxLinkBytes", 4096, 65536) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }
}
