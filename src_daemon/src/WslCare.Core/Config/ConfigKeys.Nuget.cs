namespace WslCare.Core.Config;

/// <summary>The <c>nuget.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key.</summary>
public static partial class ConfigKeys
{
    public static class Nuget
    {
        /// <summary>dotnet nuget locals http-cache --clear. Default 600.</summary>
        public static readonly ConfigKey.IntKey ClearTimeoutSeconds = new("nuget.clearTimeoutSeconds", 60, 600) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }
}
