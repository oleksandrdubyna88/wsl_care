namespace WslCare.Core.Config;

/// <summary>The <c>archive.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key. Its older keys stay in <c>ConfigKeys.cs</c>.</summary>
public static partial class ConfigKeys
{
    /// <summary>The AI-session archive (plan §15r, E9.S0). Group A: the ages and the base's free space, the user's to choose;
    /// group B: what bounds a run root starts and waits on — the budget, the ceilings, the caps of what root reads back.</summary>
    public static partial class Archive
    {
        /// <summary>Phase 2 (§15r D2) removes a source only this long after its copy was indexed, once the copy is read again. Default 24.</summary>
        public static readonly ConfigKey.IntKey RemoveAfterHours = new("archive.removeAfterHours", 1, 720) { Trust = KeyTrust.Higher };

        /// <summary>Days kept between a source's removal and the agent's own deletion (§15r D10). Default 7.</summary>
        public static readonly ConfigKey.IntKey MarginDays = new("archive.marginDays", 1, 365) { Trust = KeyTrust.Higher };

        /// <summary>The shortest own-retention of an archived agent — Claude Code's cleanupPeriodDays default; raise it with that
        /// setting (§15r D10). Default 30.</summary>
        public static readonly ConfigKey.IntKey AgentRetentionDays = new("archive.agentRetentionDays", 3, 3650) { Trust = KeyTrust.Lower };

        /// <summary>Within this many days of an agent's retention the timer does not wait for an idle machine (§15r D8). Default 7.</summary>
        public static readonly ConfigKey.IntKey UrgentWithinDays = new("archive.urgentWithinDays", 0, 365) { Trust = KeyTrust.Higher };

        /// <summary>The base keeps this much free; a session that would go below it is not started (§15r D7). Default 5.</summary>
        public static readonly ConfigKey.IntKey MinFreeGb = new("archive.minFreeGb", 0, GbCeiling) { Trust = KeyTrust.Higher };

        /// <summary>The copy's read size. Default 1024.</summary>
        public static readonly ConfigKey.IntKey CopyBufferKib = new("archive.copyBufferKib", 64, 16384) { Trust = KeyTrust.Display };

        /// <summary>The most the archive child takes of one timer run — from the run limit's slack, never a term of its sum (§15r D8).
        /// Default 30.</summary>
        public static readonly ConfigKey.IntKey RunBudgetMinutes = new("archive.runBudgetMinutes", 1, 55) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The session in flight may finish this long past the budget. Default 5.</summary>
        public static readonly ConfigKey.IntKey FinishGraceMinutes = new("archive.finishGraceMinutes", 1, 30) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Below this much slack in a timer run A13 skips "no time left in this run". Default 2.</summary>
        public static readonly ConfigKey.IntKey MinRunMinutes = new("archive.minRunMinutes", 1, 60) { Trust = new(SafeDirection.Higher, MachineOnly: true) };

        /// <summary>The preview child's ceiling — counted in a timer run's worst case. Default 120.</summary>
        public static readonly ConfigKey.IntKey PreviewTimeoutSeconds = new("archive.previewTimeoutSeconds", 10, 600) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The one-<c>stat</c> child that asks whether the base answers at all, before the long one (§15r D1, review M11). Default 10.</summary>
        public static readonly ConfigKey.IntKey ReachabilitySeconds = new("archive.reachabilitySeconds", 1, 60) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The archive child prints a line at least this often, mid-file too — the progress watchdog counts it (§15r D8). Default 60.</summary>
        public static readonly ConfigKey.IntKey ProgressSilenceSeconds = new("archive.progressSilenceSeconds", 10, 600) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>A20's restore child's ceiling (the restore button; A19 is the idle MCP servers' since E14 S2a) — a button, never in a timer run; within the least maximum a request accepts
        /// (<c>commands.maxTimeoutHours</c>' minimum, 1 h) WITH the 60 s a command's ceiling keeps — so at most 59 (E9.S0 review
        /// round C5). Default 59.</summary>
        public static readonly ConfigKey.IntKey RestoreLimitMinutes = new("archive.restoreLimitMinutes", 1, 59) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The most sessions one run takes — bounds the in-flight file and the answer root reads (the owner's backlog of
        /// ~4 500 sessions drains in five runs). Default 1000.</summary>
        public static readonly ConfigKey.IntKey MaxSessionsPerRun = new("archive.maxSessionsPerRun", 1, 100000) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The most entries one A20 press restores (plan §15r E9.S4 plan round, finding 0): the ids reach the restore child as
        /// ONE argument, so its ceiling keeps that argument far below the kernel's per-argument limit (128 KiB; 5 000 ids of 16 hex and a
        /// comma are 85 000 bytes). Default 1000.</summary>
        public static readonly ConfigKey.IntKey MaxRestoreEntries = new("archive.maxRestoreEntries", 1, 5000) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>One month index read. Default 67108864.</summary>
        public static readonly ConfigKey.IntKey MaxIndexBytes = new("archive.maxIndexBytes", 1048576, 268435456) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The archive's local state files (the in-flight file, the summary, the restored map) — the in-flight file holds
        /// the sessions of every run still waiting for its removal (E9.S0 review round C3); 16 MiB so the hourly timer (25 waiting
        /// runs × 1000 × 600 B) stays valid under the defaults. Default 16777216.</summary>
        public static readonly ConfigKey.IntKey MaxStateFileBytes = new("archive.maxStateFileBytes", 65536, 67108864) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The archive child's answer root reads. Default 1048576.</summary>
        public static readonly ConfigKey.IntKey ChildOutputCapBytes = new("archive.childOutputCapBytes", 65536, 16777216) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>One progress line root reads (it carries no name). Default 256.</summary>
        public static readonly ConfigKey.IntKey ProgressLineMaxBytes = new("archive.progressLineMaxBytes", 64, 4096) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The open-file scan (<c>/proc/*/fd</c>) or the Restart Manager query of one run. Default 20.</summary>
        public static readonly ConfigKey.IntKey InUseScanSeconds = new("archive.inUseScanSeconds", 1, 120) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>An EMPTY side lease is read again after this wait: a creator that is alive writes it right after its exclusive
        /// create, so one still empty is a run that died in between and is taken over (plan §15r D4, E9.S2b). Longer is safer.
        /// Default 2000.</summary>
        public static readonly ConfigKey.IntKey LeaseSettleMilliseconds = new("archive.leaseSettleMilliseconds", 100, 30000) { Trust = new(SafeDirection.Higher, MachineOnly: true) };

        /// <summary>An in-flight entry phase 2 keeps waiting (its index unreadable, a line unverified, its folder behind a link) this many
        /// days after it was archived is let go: dropped, its quarantined files renamed back, its source left where it is (correctness
        /// review M5). Default 14.</summary>
        public static readonly ConfigKey.IntKey KeptEntryDays = new("archive.keptEntryDays", 1, 365) { Trust = KeyTrust.Display };

        /// <summary>A restored entry leaves <c>restored.json</c> this many days after its restore when another is added (E9.S3 own review
        /// round C-6): past it the session is either archived again (it left already) or gone. Default 180.</summary>
        public static readonly ConfigKey.IntKey RestoredKeepDays = new("archive.restoredKeepDays", 1, 3650) { Trust = KeyTrust.Display };
    }
}
