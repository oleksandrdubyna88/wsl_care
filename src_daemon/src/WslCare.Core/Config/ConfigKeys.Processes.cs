namespace WslCare.Core.Config;

/// <summary>The <c>processes.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key. Its older keys stay in <c>ConfigKeys.cs</c>.</summary>
public static partial class ConfigKeys
{
    public static partial class Processes
    {
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

        /// <summary>The poll slice while waiting for processes to end. Default 200.</summary>
        public static readonly ConfigKey.IntKey SignalSliceMilliseconds = new("processes.signalSliceMilliseconds", 50, 1000) { Trust = new(SafeDirection.None, MachineOnly: true) };

        /// <summary>A21 (plan E14 S7b.2): the longest one scan of every process's fd table may take — past it the scan is
        /// inconclusive and every relay is kept (lower is safer). The watch's worst case counts it three times (its preview, the act's
        /// preview, the re-check). Default 10000 — provisional: every A21 preview reports what its scan cost, and the dry-run
        /// preview on the owner's machine sets the measured default (the S7b.2 own code review, finding 1).</summary>
        public static readonly ConfigKey.IntKey FdScanMilliseconds = new("processes.fdScanMilliseconds", 100, 30000) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }
}
