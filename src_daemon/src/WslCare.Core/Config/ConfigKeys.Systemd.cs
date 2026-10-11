namespace WslCare.Core.Config;

/// <summary>The <c>systemd.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key.</summary>
public static partial class ConfigKeys
{
    public static class Systemd
    {
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
