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

        /// <summary>E19.S1: <c>disk.volumeFree</c> WARNS when a lettered Windows volume has less than this many GiB free (on a volume of at
        /// least <see cref="VolumeFreeAbsoluteMinSizeGib"/>) — or less than <see cref="VolumeFreeWarnPercent"/> free. Default 30 — a STARTING
        /// VALUE: D: was 30.6 GiB free four days before it hit 0 (research/2026-10-10_disk.md D1; PLAN_disk_hygiene_windows.md §1), so the warning arrives before
        /// that margin is spent; a floor, not a measured need.</summary>
        public static readonly ConfigKey.IntKey VolumeFreeWarnGib = new("disk.volumeFreeWarnGib", 1, 10240) { Trust = KeyTrust.Display };

        /// <summary>E19.S1: <c>disk.volumeFree</c> is CRITICAL when a lettered volume has less than this many GiB free (on a volume of at
        /// least <see cref="VolumeFreeAbsoluteMinSizeGib"/>) — or less than <see cref="VolumeFreeCriticalPercent"/> free; only a critical
        /// volume marks the status bar. Default 10 — a STARTING VALUE: a worktree's build output is about 1.7–2.4 GB, so 10 GiB is four or
        /// five builds. Held below the warn floor by <see cref="NumberRules"/>.</summary>
        public static readonly ConfigKey.IntKey VolumeFreeCriticalGib = new("disk.volumeFreeCriticalGib", 1, 10240) { Trust = KeyTrust.Display };

        /// <summary>E19.S1: <c>disk.volumeFree</c> warns when a judged volume has less than this share free, whatever its size — the only
        /// floor of a volume under <see cref="VolumeFreeAbsoluteMinSizeGib"/>. Default 10 — a STARTING VALUE: on the 2.4 TiB system
        /// volume it warns at about 245 GiB, acceptable for a warning.</summary>
        public static readonly ConfigKey.IntKey VolumeFreeWarnPercent = new("disk.volumeFreeWarnPercent", 1, 90) { Trust = KeyTrust.Display };

        /// <summary>E19.S1: <c>disk.volumeFree</c> is critical when a judged volume has less than this share free. Default 3 — a STARTING
        /// VALUE, not measured. Held below the warn share by <see cref="NumberRules"/>.</summary>
        public static readonly ConfigKey.IntKey VolumeFreeCriticalPercent = new("disk.volumeFreeCriticalPercent", 1, 90) { Trust = KeyTrust.Display };

        /// <summary>E19.S1: a lettered volume smaller than this many GiB is NOT judged by <c>disk.volumeFree</c> — the keyboard's 29.7 MiB
        /// USB stick (D1) must never warn. Default 8 — a STARTING VALUE: well above that stick, below any system or data volume.
        /// Held at or below <see cref="VolumeFreeAbsoluteMinSizeGib"/> by <see cref="NumberRules"/>.</summary>
        public static readonly ConfigKey.IntKey VolumeFreeMinSizeGib = new("disk.volumeFreeMinSizeGib", 1, 1024) { Trust = KeyTrust.Display };

        /// <summary>E19.S1: the GiB floors apply only to a volume of at least this many GiB; a smaller one is judged by percent alone (a 16
        /// GiB volume with 12 GiB free would otherwise warn for ever, and a 9 GiB one be critical for ever, whatever it holds). Default
        /// 100 — a STARTING VALUE.</summary>
        public static readonly ConfigKey.IntKey VolumeFreeAbsoluteMinSizeGib = new("disk.volumeFreeAbsoluteMinSizeGib", 8, 10240) { Trust = KeyTrust.Display };

        /// <summary>At most this many letters in <see cref="VolumeFreeIgnoreLetters"/> — one per drive letter A-Z, a schema limit, part of the
        /// contract other tools write against (<c>contracts/config-keys.json</c>).</summary>
        public const int MaxIgnoredLetters = 26;

        /// <summary>E19.S1: drive letters <c>disk.volumeFree</c> does NOT judge (a backup disk that is meant to be full) — each one capital
        /// letter A-Z. Default none. A user key: it only quiets a warning, it changes no action; the verdict's reason still names an ignored
        /// volume.</summary>
        public static readonly ConfigKey.TextListKey VolumeFreeIgnoreLetters = new(
            "disk.volumeFreeIgnoreLetters",
            [],
            new TextRule.Matching("^[A-Z]$", "a drive letter A-Z"),
            MaxIgnoredLetters)
        { Trust = KeyTrust.Display };
    }
}
