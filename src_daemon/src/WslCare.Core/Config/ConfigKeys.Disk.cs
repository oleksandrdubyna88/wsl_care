namespace WslCare.Core.Config;

/// <summary>The <c>disk.*</c> keys (E15.S1, E15.S2 of PLAN_disk_health.md) — in their own file so <c>ConfigKeys.Numbers.cs</c> does not
/// grow (own review, 2026-10-11; the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every
/// other number key.</summary>
public static partial class ConfigKeys
{
    public static class Disk
    {
        /// <summary>E15.S1: the Windows disk script reads the System log's storage events of this many days back — baked into the
        /// fixed script as an integer, never as text. Default 60 (research/2026-10-10_disk.md D4).</summary>
        public static readonly ConfigKey.IntKey EventsSinceDays = new("disk.eventsSinceDays", 1, 365) { Trust = KeyTrust.Display };

        /// <summary>E15.S1: at most this many of those events are read (<c>Get-WinEvent -MaxEvents</c>, newest first) — it bounds
        /// the work root's script does, so machine-only. Default 5000.</summary>
        public static readonly ConfigKey.IntKey EventsMaxRead = new("disk.eventsMaxRead", 100, 100000) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>E15.S1's code round: at most this many day / provider / id / disk groups of those events are PRINTED (newest
        /// first), so the answer always fits the health output cap and reaches its end line; past it the answer says how many
        /// groups there were. Its maximum keeps even the smallest <c>health.outputCapBytes</c> clear. Default 500.</summary>
        public static readonly ConfigKey.IntKey EventGroupsMax = new("disk.eventGroupsMax", 10, 800) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>E15.S2: <c>disk.vhdxGap</c> warns when a virtual disk file is more than this many GiB larger than what its guest
        /// uses. Default 20 — a STARTING VALUE: under half of the 43.7 GiB gap measured on Ubuntu's VHDX
        /// (research/2026-10-10_disk.md D2/D3), so that measured case warns.</summary>
        public static readonly ConfigKey.IntKey VhdxGapWarnGib = new("disk.vhdxGapWarnGib", 1, 1024) { Trust = KeyTrust.Display };

        /// <summary>E15.S2: <c>disk.vhdxGrowth</c> warns when a virtual disk file grows faster than this many GiB a day. Default 10 —
        /// a STARTING VALUE, not measured: D3 has only write volume, and writes are not growth; the first weeks of growth figures
        /// calibrate it.</summary>
        public static readonly ConfigKey.IntKey VhdxGrowthWarnGibPerDay = new("disk.vhdxGrowthWarnGibPerDay", 1, 1024) { Trust = KeyTrust.Display };

        /// <summary>E15.S2: <c>disk.hostFree</c> warns when a volume that holds virtual disks has less than this many GiB free. Default
        /// 100 — a STARTING VALUE: about 4 % of the 2.4 TiB system volume (D1); a floor, not a measured need.</summary>
        public static readonly ConfigKey.IntKey HostFreeWarnGib = new("disk.hostFreeWarnGib", 1, 10240) { Trust = KeyTrust.Display };

        /// <summary>E15.S2: a growth per day is computed only from two sizes at least this many hours apart; closer, it is
        /// unavailable ("too close to the previous measurement"). Default 1 — a STARTING VALUE, not measured: it keeps the
        /// extrapolation factor at 24 or below.</summary>
        public static readonly ConfigKey.IntKey GrowthMinHours = new("disk.growthMinHours", 1, 48) { Trust = KeyTrust.Display };
    }
}
