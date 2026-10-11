namespace WslCare.Core.Config;

/// <summary>The <c>stops.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key.</summary>
public static partial class ConfigKeys
{
    public static class Stops
    {
        /// <summary>Bytes of a stop marker read. Default 4096.</summary>
        public static readonly ConfigKey.IntKey MaxMarkerBytes = new("stops.maxMarkerBytes", 256, 4096) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }
}
