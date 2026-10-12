namespace WslCare.Core.Config;

/// <summary>The <c>systemd.*</c> keys: the number keys (E7.S2c), which join <c>NumberKeys()</c> like every other number key, and
/// the failed units the verdict does not count (2026-10-12). One file per key group, split from <c>ConfigKeys.Numbers.cs</c> (the
/// doctrine caps a file at 800 lines).</summary>
public static partial class ConfigKeys
{
    public static class Systemd
    {
        /// <summary>At most this many units in <see cref="IgnoredFailedUnits"/> — a schema limit, part of the contract other tools write
        /// against (<c>contracts/config-keys.json</c>).</summary>
        public const int MaxIgnoredFailedUnits = 32;

        /// <summary>The failed units <c>systemd.failedUnits</c> does NOT count — each a systemd unit name (letters, digits and
        /// <c>@ : _ . - \</c>, then a unit type), never a pattern. Default <c>getty@tty1.service</c>: WSL has no tty1, so that unit hits
        /// its start limit on every boot (the first live full run, 2026-10-12). The verdict's reason still names an ignored unit. A
        /// user key: it only quiets a warning, it changes no action.</summary>
        public static readonly ConfigKey.TextListKey IgnoredFailedUnits = new(
            "systemd.ignoredFailedUnits",
            [],
            new TextRule.SystemdUnitName(),
            MaxIgnoredFailedUnits)
        { Trust = KeyTrust.Display };

        /// <summary>systemctl / journalctl / timedatectl reads. Default 15.</summary>
        public static readonly ConfigKey.IntKey TimeoutSeconds = new("systemd.timeoutSeconds", 1, 15) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>A journal search. Default 30.</summary>
        public static readonly ConfigKey.IntKey SearchTimeoutSeconds = new("systemd.searchTimeoutSeconds", 5, 30) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Output kept of a systemctl / journalctl read. Default 1048576.</summary>
        public static readonly ConfigKey.IntKey OutputCapBytes = new("systemd.outputCapBytes", 65536, 1048576) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Output kept of a journal search. Default 4194304.</summary>
        public static readonly ConfigKey.IntKey SearchOutputCapBytes = new("systemd.searchOutputCapBytes", 262144, 4194304) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Output kept of systemctl start / stop. Default 65536.</summary>
        public static readonly ConfigKey.IntKey UnitOutputCapBytes = new("systemd.unitOutputCapBytes", 4096, 65536) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>systemctl start --no-block. Default 30.</summary>
        public static readonly ConfigKey.IntKey UnitStartTimeoutSeconds = new("systemd.unitStartTimeoutSeconds", 5, 30) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>systemctl stop: at least units.stopTimeoutSeconds + 30 (raise-only). Default 120.</summary>
        public static readonly ConfigKey.IntKey UnitStopTimeoutSeconds = new("systemd.unitStopTimeoutSeconds", 120, 600) { Trust = new(SafeDirection.Higher, MachineOnly: true) };
    }
}
