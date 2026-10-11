namespace WslCare.Core.Config;

/// <summary>The <c>buildServers.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key. Its older keys stay in <c>ConfigKeys.cs</c>.</summary>
public static partial class ConfigKeys
{
    public static partial class BuildServers
    {
        /// <summary>dotnet build-server shutdown. Default 120.</summary>
        public static readonly ConfigKey.IntKey ShutdownTimeoutSeconds = new("buildServers.shutdownTimeoutSeconds", 10, 120) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>E14 S3: A3's timer runs <c>dotnet build-server shutdown</c> — which stops EVERY server of the user — only when
        /// every one used no CPU for this many minutes, measured by identity over the timer's CPU history. A longer window stops
        /// less, so higher is safer. Default 60; on the 4-hour timer it is a floor.</summary>
        public static readonly ConfigKey.IntKey IdleMinutes = new("buildServers.idleMinutes", 10, 10080) { Trust = KeyTrust.Higher };
    }
}
