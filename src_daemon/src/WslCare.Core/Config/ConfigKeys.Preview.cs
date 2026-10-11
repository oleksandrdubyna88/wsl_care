namespace WslCare.Core.Config;

/// <summary>The <c>preview.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key.</summary>
public static partial class ConfigKeys
{
    public static partial class Preview
    {
        /// <summary>How many objects a preview names (the confirmation modal). Default 20.</summary>
        public static readonly ConfigKey.IntKey MaxItems = new("preview.maxItems", 1, 100) { Trust = KeyTrust.Display };
    }
}
