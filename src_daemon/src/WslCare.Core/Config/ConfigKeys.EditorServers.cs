namespace WslCare.Core.Config;

/// <summary>The <c>editorServers.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key.</summary>
public static partial class ConfigKeys
{
    public static partial class EditorServers
    {
        /// <summary>A14 keeps this many newest builds of each editor server. Default 2.</summary>
        public static readonly ConfigKey.IntKey KeepNewest = new("editorServers.keepNewest", 1, 20) { Trust = KeyTrust.Higher };
    }
}
