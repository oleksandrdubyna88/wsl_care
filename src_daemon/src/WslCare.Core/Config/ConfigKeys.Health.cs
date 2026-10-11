namespace WslCare.Core.Config;

/// <summary>The <c>health.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key.</summary>
public static partial class ConfigKeys
{
    public static partial class Health
    {
        /// <summary>The Windows clock probe. Default 20.</summary>
        public static readonly ConfigKey.IntKey WindowsClockTimeoutSeconds = new("health.windowsClockTimeoutSeconds", 5, 20) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>E15.S1: the second fixed interop script, the Windows disk facts (only the full run starts it). Measured
        /// 2026-10-10 from Windows PowerShell 5.1, unelevated: 2.4–4.4 s warm, 10.7 s cold. Default 30.</summary>
        public static readonly ConfigKey.IntKey WindowsDiskTimeoutSeconds = new("health.windowsDiskTimeoutSeconds", 5, 120) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>snap list --all. Default 30.</summary>
        public static readonly ConfigKey.IntKey SnapTimeoutSeconds = new("health.snapTimeoutSeconds", 5, 30) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Output kept of the health probes. Default 1048576.</summary>
        public static readonly ConfigKey.IntKey OutputCapBytes = new("health.outputCapBytes", 65536, 1048576) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Kernel log lines copied into a report. Default 5.</summary>
        public static readonly ConfigKey.IntKey KernelLinesKept = new("health.kernelLinesKept", 0, 5) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Characters of each copied kernel line. Default 200.</summary>
        public static readonly ConfigKey.IntKey KernelLineChars = new("health.kernelLineChars", 0, 200) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Bytes of .wslconfig read. Default 1048576.</summary>
        public static readonly ConfigKey.IntKey MaxWslConfigBytes = new("health.maxWslConfigBytes", 65536, 1048576) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }
}
