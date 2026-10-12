namespace WslCare.Core.Config;

/// <summary>The <c>requests.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key.</summary>
public static partial class ConfigKeys
{
    public static class Requests
    {
        /// <summary>Request files one reader opens (at least requests.maxQueued). Default 64.</summary>
        public static readonly ConfigKey.IntKey MaxRead = new("requests.maxRead", 1, 64) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Requests waiting at once; one more is refused (exit 73). Default 32.</summary>
        public static readonly ConfigKey.IntKey MaxQueued = new("requests.maxQueued", 1, 32) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The largest request file root reads. Default 1048576.</summary>
        public static readonly ConfigKey.IntKey MaxBytes = new("requests.maxBytes", 65536, 1048576) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>How long a request waits for its unit before it is swept (raise-only). Default 60.</summary>
        public static readonly ConfigKey.IntKey GraceSeconds = new("requests.graceSeconds", 60, 600) { Trust = new(SafeDirection.Higher, MachineOnly: true) };

        /// <summary>How far a request may be ahead of the clock before it is stale (published in status --json limits). Default 300.</summary>
        public static readonly ConfigKey.IntKey FutureSkewSeconds = new("requests.futureSkewSeconds", 60, 3600) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>How long an ACCEPTED detached run waits for THE run lock before it is recorded refused — a --detach check holds
        /// the lock for its sweep and count (retro round over PR #11, O1); 0 is the old refuse-at-once. Default 30.</summary>
        public static readonly ConfigKey.IntKey LockWaitSeconds = new("requests.lockWaitSeconds", 0, 120) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }
}
