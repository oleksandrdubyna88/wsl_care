namespace WslCare.Core.Config;

/// <summary>The <c>toolCaches.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key.</summary>
public static partial class ConfigKeys
{
    public static class ToolCaches
    {
        /// <summary>A17 trims when one tool cache is above this size. Default 5.</summary>
        public static readonly ConfigKey.IntKey TriggerGb = new("toolCaches.triggerGb", 0, 100000) { Trust = KeyTrust.Higher };

        /// <summary>pnpm store prune / uv cache prune / pip cache purge. Default 600.</summary>
        public static readonly ConfigKey.IntKey TrimTimeoutSeconds = new("toolCaches.trimTimeoutSeconds", 60, 600) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>A tool asked where its cache is. Default 30.</summary>
        public static readonly ConfigKey.IntKey WhereTimeoutSeconds = new("toolCaches.whereTimeoutSeconds", 5, 30) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Output kept of a tool's where-is-your-cache answer. Default 65536.</summary>
        public static readonly ConfigKey.IntKey WhereOutputCapBytes = new("toolCaches.whereOutputCapBytes", 4096, 65536) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }
}
