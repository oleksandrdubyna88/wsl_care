namespace WslCare.Core.Config;

/// <summary>The <c>mcpWatchdog.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key.</summary>
public static partial class ConfigKeys
{
    public static partial class McpWatchdog
    {
        /// <summary>A19 (plan E14 S2a): an MCP server with NO CPU for at least this long, measured by identity over the timer's CPU
        /// history, is stopped — the owner's "idle > 60 min" of 2026-10-08. It decides what ends, so a user layer may only lengthen
        /// it. Default 60.</summary>
        public static readonly ConfigKey.IntKey IdleMinutes = new("mcpWatchdog.idleMinutes", 10, 10080) { Trust = KeyTrust.Higher };

        /// <summary>A19: the same for a server whose agent died (re-parented to init) — nobody can talk to it any more (the owner's
        /// Q-M3). Default 10.</summary>
        public static readonly ConfigKey.IntKey OrphanIdleMinutes = new("mcpWatchdog.orphanIdleMinutes", 1, 10080) { Trust = KeyTrust.Higher };

        /// <summary>A19's busy half (plan E14 S2b): an MCP server busy without a log write for at least this long — a chain of interval
        /// readings in root's CPU ledger — is stopped. It decides what ends, so a user layer may only lengthen it. Default 30 (an
        /// assumption, the coordinator's; the owner may change it).</summary>
        public static readonly ConfigKey.IntKey BusyMinutes = new("mcpWatchdog.busyMinutes", 10, 10080) { Trust = KeyTrust.Higher };

        /// <summary>The watch timer's period (<c>wsl-care-watch.timer</c>, plan E14 S2b): how often root samples the MCP servers into
        /// its ledger and lets A19 act. It must stay under <c>mcpServers.cpuIntervalMaxMinutes</c>, or no sample finds a baseline.
        /// Machine-only (it is a unit's value). Default 5.</summary>
        public static readonly ConfigKey.IntKey PeriodMinutes = new("mcpWatchdog.periodMinutes", 2, 15) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The longest one watch run may take (<c>wsl-care-watch.service</c>'s <c>TimeoutStartSec</c>): a sample, the CPU
        /// window, and A19's signals with their grace. Machine-only. Default 10.</summary>
        public static readonly ConfigKey.IntKey RunLimitMinutes = new("mcpWatchdog.runLimitMinutes", 2, 60) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }
}
