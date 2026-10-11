namespace WslCare.Core.Config;

/// <summary>The <c>memory.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key.</summary>
public static partial class ConfigKeys
{
    public static partial class Memory
    {
        /// <summary>sync before dropping caches. Default 120.</summary>
        public static readonly ConfigKey.IntKey SyncTimeoutSeconds = new("memory.syncTimeoutSeconds", 10, 120) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>sysctl vm.drop_caches. Default 30.</summary>
        public static readonly ConfigKey.IntKey DropCachesTimeoutSeconds = new("memory.dropCachesTimeoutSeconds", 5, 30) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>sysctl vm.compact_memory. Default 120.</summary>
        public static readonly ConfigKey.IntKey CompactTimeoutSeconds = new("memory.compactTimeoutSeconds", 10, 120) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }
}
