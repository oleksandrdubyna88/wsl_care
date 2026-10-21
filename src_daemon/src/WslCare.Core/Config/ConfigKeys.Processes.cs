namespace WslCare.Core.Config;

/// <summary>The <c>processes.*</c> keys, in their own file: one file per key group, never one group in two files (the doctrine caps
/// a file at 800 lines). The older keys come first; the E7.S2c number keys after them join <c>NumberKeys()</c> like every
/// other number key.</summary>
public static partial class ConfigKeys
{
    public static class Processes
    {
        public static readonly ConfigKey.IntKey IdleOlderThanHours = new("processes.idleOlderThanHours", 0, HoursCeiling) { Trust = KeyTrust.Higher };
        /// <summary>A11's families (§15q R1.3, review B1): only the catalogue's named families — never <c>other</c>, the catch-all, which
        /// would let root end every account's idle orphans; and not <c>ai-agents</c>, whose processes are the owner's work (§15q Q13).</summary>
        public static readonly ConfigKey.TextListKey Families = new("processes.families", Collectors.ProcessFamilies.ChoosableForA11) { Trust = new(SafeDirection.Subset) };

        /// <summary>A18 (plan §15q E7.S2b, owner decision 2026-10-05): an AI agent's orphaned process is ended only after this many
        /// hours with NO CPU, measured by identity, and with no session of its agent written in that window. A longer window is
        /// stricter.</summary>
        public static readonly ConfigKey.IntKey AiAgentsIdleHours = new("processes.aiAgentsIdleHours", 1, 168) { Trust = KeyTrust.Higher };

        /// <summary>A11: a suspect must use no CPU in this window. Default 5.</summary>
        public static readonly ConfigKey.IntKey CpuWindowSeconds = new("processes.cpuWindowSeconds", 5, 60) { Trust = KeyTrust.Higher };

        /// <summary>A11 and A18: SIGKILL this long after SIGTERM (raise-only: plan section 5 fixes 10 s as the least). Default 10.</summary>
        public static readonly ConfigKey.IntKey TermGraceSeconds = new("processes.termGraceSeconds", 10, 120) { Trust = KeyTrust.Higher };

        /// <summary>How many processes a full run names. Default 30.</summary>
        public static readonly ConfigKey.IntKey TopCount = new("processes.topCount", 0, 200) { Trust = KeyTrust.Display };

        /// <summary>The wait after SIGKILL before "still running". Default 5.</summary>
        public static readonly ConfigKey.IntKey KillWaitSeconds = new("processes.killWaitSeconds", 1, 30) { Trust = new(SafeDirection.None, MachineOnly: true) };

        /// <summary>Characters of a command line a record keeps. Default 200.</summary>
        public static readonly ConfigKey.IntKey ShownCommandChars = new("processes.shownCommandChars", 0, 200) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>E15.S7 (design 4): the shortest token the entropy test judges. Lower redacts MORE, so lower is the safe direction;
        /// machine layer only, because a user layer must not loosen what root writes into a history other accounts read. Default 20 — the
        /// detectors' common floor.</summary>
        public static readonly ConfigKey.IntKey SecretMinChars = new("processes.secretMinChars", 12, 64) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
        /// <summary>E15.S7: how many SAFE leading arguments a view shows by default (<c>argumentsShown</c>). Fewer is safer. Default 2.</summary>
        public static readonly ConfigKey.IntKey ShownSafeArgs = new("processes.shownSafeArgs", 0, 8) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
        /// <summary>E15.S7: the longest argument the rule judges at all — a longer one is a script or a blob and is replaced whole; the
        /// bound is what keeps every pattern linear. A smaller bound redacts more. Default 4096.</summary>
        public static readonly ConfigKey.IntKey MaxJudgedArgChars = new("processes.maxJudgedArgChars", 1024, 65536) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
        /// <summary>The poll slice while waiting for processes to end. Default 200.</summary>
        public static readonly ConfigKey.IntKey SignalSliceMilliseconds = new("processes.signalSliceMilliseconds", 50, 1000) { Trust = new(SafeDirection.None, MachineOnly: true) };

        /// <summary>A21 (plan E14 S7b.2): the longest one scan of every process's fd table may take — past it the scan is
        /// inconclusive and every relay is kept (lower is safer). The watch's worst case counts it three times (its preview, the act's
        /// preview, the re-check). Default 10000 — provisional: every A21 preview reports what its scan cost, and the dry-run
        /// preview on the owner's machine sets the measured default (the S7b.2 own code review, finding 1).</summary>
        public static readonly ConfigKey.IntKey FdScanMilliseconds = new("processes.fdScanMilliseconds", 100, 30000) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }
}
