namespace WslCare.Core.Config;

/// <summary>The <c>commands.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key.</summary>
public static partial class ConfigKeys
{
    public static partial class Commands
    {
        /// <summary>Output kept of a command without its own cap. Default 1048576.</summary>
        public static readonly ConfigKey.IntKey OutputCapBytes = new("commands.outputCapBytes", 65536, 1048576) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The whole lookup of a program on the Windows system drive. Default 5.</summary>
        public static readonly ConfigKey.IntKey SystemDriveLookupSeconds = new("commands.systemDriveLookupSeconds", 1, 30) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>No command may run longer; every timeout key is checked against it. Default 24.</summary>
        public static readonly ConfigKey.IntKey MaxTimeoutHours = new("commands.maxTimeoutHours", 1, 24) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The wait for a command's output streams after it exits or is killed. Default 2000.</summary>
        public static readonly ConfigKey.IntKey DrainGraceMilliseconds = new("commands.drainGraceMilliseconds", 500, 10000) { Trust = new(SafeDirection.None, MachineOnly: true) };
    }
}
