namespace WslCare.Core.Config;

/// <summary>The <c>agents.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key.</summary>
public static partial class ConfigKeys
{
    public static partial class Agents
    {
        /// <summary>agents list --measure and agents probe: the whole walk; below every host call ceiling. Default 60.</summary>
        public static readonly ConfigKey.IntKey MeasureBudgetSeconds = new("agents.measureBudgetSeconds", 5, 60) { Trust = KeyTrust.Lower };

        /// <summary>The whole agent walk inside a root collect. Default 180.</summary>
        public static readonly ConfigKey.IntKey WalkBudgetSeconds = new("agents.walkBudgetSeconds", 10, 180) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Entries one session listing may see. Default 500000.</summary>
        public static readonly ConfigKey.IntKey SessionMaxEntries = new("agents.sessionMaxEntries", 1000, 500000) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Bytes of a package.json read for a version. Default 1048576.</summary>
        public static readonly ConfigKey.IntKey MaxPackageJsonBytes = new("agents.maxPackageJsonBytes", 65536, 1048576) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The whole binary lookup of one discovery. Default 10.</summary>
        public static readonly ConfigKey.IntKey LookupCeilingSeconds = new("agents.lookupCeilingSeconds", 1, 10) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }
}
