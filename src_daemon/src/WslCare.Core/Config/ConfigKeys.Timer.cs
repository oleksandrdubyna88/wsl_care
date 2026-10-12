namespace WslCare.Core.Config;

/// <summary>The <c>timer.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key.</summary>
public static partial class ConfigKeys
{
    public static class Timer
    {
        /// <summary>The timer runs dry this many days after its first run (raise-only). Default 7.</summary>
        public static readonly ConfigKey.IntKey FirstDryWindowDays = new("timer.firstDryWindowDays", 7, 3650) { Trust = new(SafeDirection.Higher, MachineOnly: true) };

        /// <summary>The timer runs a full check this often (installed as the timer drop-in). Default 4.</summary>
        public static readonly ConfigKey.IntKey PeriodHours = new("timer.periodHours", 1, 24) { Trust = new(SafeDirection.Higher, MachineOnly: true) };

        /// <summary>doctor counts the timer as not running this long after a period. Default 60.</summary>
        public static readonly ConfigKey.IntKey LateSlackMinutes = new("timer.lateSlackMinutes", 10, 1440) { Trust = new(SafeDirection.Higher, MachineOnly: true) };

        /// <summary>The timer drop-in RandomizedDelaySec. Default 5.</summary>
        public static readonly ConfigKey.IntKey RandomizedDelayMinutes = new("timer.randomizedDelayMinutes", 0, 60) { Trust = new(SafeDirection.None, MachineOnly: true) };

        /// <summary>The timer drop-in AccuracySec. Default 1.</summary>
        public static readonly ConfigKey.IntKey AccuracyMinutes = new("timer.accuracyMinutes", 1, 60) { Trust = new(SafeDirection.None, MachineOnly: true) };

        /// <summary>wsl-care.service's TimeoutStartSec: the backstop that ends a timer run as a whole (E7.S2b/S2c review C-H2; at least
        /// the derived worst case of a timer run). Default 240.</summary>
        public static readonly ConfigKey.IntKey RunLimitMinutes = new("timer.runLimitMinutes", 60, 1440) { Trust = new(SafeDirection.Higher, MachineOnly: true) };

        /// <summary>A timer run within this many minutes of boot first waits until the machine has been up that long
        /// (PLAN_boot_settle.md; 0 = off). Default 15 — provisional, about three times the one measured busy boot of 2026-10-10.</summary>
        public static readonly ConfigKey.IntKey BootDelayMinutes = new("timer.bootDelayMinutes", 0, 120) { Trust = new(SafeDirection.Higher, MachineOnly: true) };

        /// <summary>The longest a timer run waits while S6 says the machine is busy, then it runs anyway (PLAN_boot_settle.md; 0 = off).
        /// Default 20 — provisional, no measurement yet (the run record's <c>settled</c> measures it).</summary>
        public static readonly ConfigKey.IntKey BusyWaitMinutes = new("timer.busyWaitMinutes", 0, 120) { Trust = new(SafeDirection.Higher, MachineOnly: true) };

        /// <summary>How often a waiting timer run asks S6 again (PLAN_boot_settle.md). Default 60.</summary>
        public static readonly ConfigKey.IntKey BusyCheckSeconds = new("timer.busyCheckSeconds", 10, 600) { Trust = new(SafeDirection.None, MachineOnly: true) };
    }
}
