namespace WslCare.Core.Config;

/// <summary>The <c>config.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key.</summary>
public static partial class ConfigKeys
{
    public static class ConfigLayerLimits
    {
        /// <summary>Bytes of the user layer read (the machine layer is read with this range maximum). Default 262144.</summary>
        public static readonly ConfigKey.IntKey MaxLayerBytes = new("config.maxLayerBytes", 4096, 262144) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }
}
