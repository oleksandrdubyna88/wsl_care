namespace WslCare.Core.Config;

/// <summary>The <c>snap.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key.</summary>
public static partial class ConfigKeys
{
    public static class Snap
    {
        /// <summary>snap remove --revision. Default 300.</summary>
        public static readonly ConfigKey.IntKey RemoveTimeoutSeconds = new("snap.removeTimeoutSeconds", 30, 300) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }
}
