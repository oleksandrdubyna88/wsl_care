namespace WslCare.Core.Config;

/// <summary>The <c>journal.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key. Its older keys stay in <c>ConfigKeys.cs</c>.</summary>
public static partial class ConfigKeys
{
    public static partial class Journal
    {
        /// <summary>The journal warns, and A10 vacuums, above this size. Default 1.</summary>
        public static readonly ConfigKey.IntKey MaxGb = new("journal.maxGb", 0, 100000) { Trust = KeyTrust.Higher };

        /// <summary>journalctl --vacuum-time. Default 300.</summary>
        public static readonly ConfigKey.IntKey VacuumTimeoutSeconds = new("journal.vacuumTimeoutSeconds", 30, 300) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }
}
