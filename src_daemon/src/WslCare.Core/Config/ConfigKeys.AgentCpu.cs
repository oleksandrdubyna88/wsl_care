namespace WslCare.Core.Config;

/// <summary>The <c>agentCpu.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key.</summary>
public static partial class ConfigKeys
{
    public static partial class AgentCpu
    {
        /// <summary>The timer's CPU history (A18, A19, A3's timer): the most process identities kept. Default 512.</summary>
        public static readonly ConfigKey.IntKey MaxEntries = new("agentCpu.maxEntries", 16, 512) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The timer's CPU history (A18, A19, A3's timer): the most bytes read (a full 512-entry history with both clocks is ~157 KiB, review A-M4). Default 262144.</summary>
        public static readonly ConfigKey.IntKey MaxBytes = new("agentCpu.maxBytes", 16384, 262144) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }
}
