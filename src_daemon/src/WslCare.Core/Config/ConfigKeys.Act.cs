namespace WslCare.Core.Config;

/// <summary>The <c>act.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key.</summary>
public static partial class ConfigKeys
{
    public static partial class Act
    {
        /// <summary>Names in one shown list (--volume, --only, a request). Default 10000.</summary>
        public static readonly ConfigKey.IntKey MaxShownNames = new("act.maxShownNames", 1, 10000) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Bytes of a list on stdin, an --only file or a request file (at least 67 bytes a shown name). Default 1048576.</summary>
        public static readonly ConfigKey.IntKey MaxListBytes = new("act.maxListBytes", 65536, 1048576) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The wait for the end of a list on stdin. Default 10.</summary>
        public static readonly ConfigKey.IntKey StdinTimeoutSeconds = new("act.stdinTimeoutSeconds", 1, 10) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }
}
