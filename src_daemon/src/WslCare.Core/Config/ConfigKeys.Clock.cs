namespace WslCare.Core.Config;

/// <summary>The <c>clock.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key. Its older keys stay in <c>ConfigKeys.cs</c>.</summary>
public static partial class ConfigKeys
{
    public static partial class Clock
    {
        /// <summary>A16 corrects the clock at most once in this time. Default 60.</summary>
        public static readonly ConfigKey.IntKey MinimumGapMinutes = new("clock.minimumGapMinutes", 60, 1440) { Trust = KeyTrust.Higher };

        /// <summary>A16 acts only on two drift observations at least this far apart. Default 5.</summary>
        public static readonly ConfigKey.IntKey DriftObservationsApartMinutes = new("clock.driftObservationsApartMinutes", 1, 1440) { Trust = KeyTrust.Higher };

        /// <summary>hwclock -s / chronyc makestep. Default 30.</summary>
        public static readonly ConfigKey.IntKey StepTimeoutSeconds = new("clock.stepTimeoutSeconds", 1, 30) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The clock reference's HEAD (curl's <c>--max-time</c> and its kill ceiling). Default 10.</summary>
        public static readonly ConfigKey.IntKey ReferenceTimeoutSeconds = new("clock.referenceTimeoutSeconds", 1, 30) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }
}
