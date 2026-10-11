namespace WslCare.Core.Config;

/// <summary>The <c>npm.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key. Its older keys stay in <c>ConfigKeys.cs</c>.</summary>
public static partial class ConfigKeys
{
    public static partial class Npm
    {
        /// <summary>npm cache clean. Default 600.</summary>
        public static readonly ConfigKey.IntKey CleanTimeoutSeconds = new("npm.cleanTimeoutSeconds", 60, 600) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }
}
