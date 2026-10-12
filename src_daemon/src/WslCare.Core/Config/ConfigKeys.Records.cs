namespace WslCare.Core.Config;

/// <summary>The <c>records.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key.</summary>
public static partial class ConfigKeys
{
    public static class Records
    {
        /// <summary>Characters of a tool's message a record keeps. Default 300.</summary>
        public static readonly ConfigKey.IntKey MaxReasonChars = new("records.maxReasonChars", 0, 300) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The wait for a record file lock. Default 5.</summary>
        public static readonly ConfigKey.IntKey LockTimeoutSeconds = new("records.lockTimeoutSeconds", 1, 30) { Trust = new(SafeDirection.None, MachineOnly: true) };

        /// <summary>Bytes of one of root's small state files read (running.json, a run detail, the seen volumes). Default 1048576.</summary>
        public static readonly ConfigKey.IntKey MaxStateFileBytes = new("records.maxStateFileBytes", 65536, 16777216) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Bytes of history.jsonl or a container-starts file read. Default 268435456.</summary>
        public static readonly ConfigKey.IntKey MaxHistoryBytes = new("records.maxHistoryBytes", 67108864, 1073741824) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }
}
