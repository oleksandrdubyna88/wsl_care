namespace WslCare.Core.Config;

/// <summary>The <c>files.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key.</summary>
public static partial class ConfigKeys
{
    public static partial class FileLocks
    {
        /// <summary>Windows: how long a busy rename is retried. Default 2000.</summary>
        public static readonly ConfigKey.IntKey RenameRetryMilliseconds = new("files.renameRetryMilliseconds", 100, 10000) { Trust = new(SafeDirection.None, MachineOnly: true) };

        /// <summary>Windows: the pause between those tries. Default 10.</summary>
        public static readonly ConfigKey.IntKey RenameRetrySleepMilliseconds = new("files.renameRetrySleepMilliseconds", 1, 1000) { Trust = new(SafeDirection.None, MachineOnly: true) };

        /// <summary>The least random pause between lock tries. Default 5.</summary>
        public static readonly ConfigKey.IntKey LockJitterMinMilliseconds = new("files.lockJitterMinMilliseconds", 1, 100) { Trust = new(SafeDirection.None, MachineOnly: true) };

        /// <summary>The most random pause between lock tries. Default 25.</summary>
        public static readonly ConfigKey.IntKey LockJitterMaxMilliseconds = new("files.lockJitterMaxMilliseconds", 2, 1000) { Trust = new(SafeDirection.None, MachineOnly: true) };
    }
}
