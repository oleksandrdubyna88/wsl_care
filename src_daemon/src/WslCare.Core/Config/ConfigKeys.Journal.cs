namespace WslCare.Core.Config;

/// <summary>The <c>journal.*</c> keys, in their own file: one file per key group, never one group in two files (the doctrine caps
/// a file at 800 lines). The older keys come first; the E7.S2c number keys after them join <c>NumberKeys()</c> like every
/// other number key.</summary>
public static partial class ConfigKeys
{
    public static class Journal
    {
        public static readonly ConfigKey.IntKey KeepDays = new("journal.keepDays", 1, DaysCeiling) { Trust = KeyTrust.Higher };

        /// <summary>The journal warns, and A10 vacuums, above this size. Default 1.</summary>
        public static readonly ConfigKey.IntKey MaxGb = new("journal.maxGb", 0, 100000) { Trust = KeyTrust.Higher };

        /// <summary>journalctl --vacuum-time. Default 300.</summary>
        public static readonly ConfigKey.IntKey VacuumTimeoutSeconds = new("journal.vacuumTimeoutSeconds", 30, 300) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }
}
