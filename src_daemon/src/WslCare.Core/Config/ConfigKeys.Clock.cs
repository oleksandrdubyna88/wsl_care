namespace WslCare.Core.Config;

/// <summary>The <c>clock.*</c> keys, in their own file: one file per key group, never one group in two files (the doctrine caps
/// a file at 800 lines). The older keys come first; the E7.S2c number keys after them join <c>NumberKeys()</c> like every
/// other number key.</summary>
public static partial class ConfigKeys
{
    public static class Clock
    {
        public static readonly ConfigKey.IntKey MaxDriftSeconds = new("clock.maxDriftSeconds", 1, 3600) { Trust = KeyTrust.Higher };

        /// <summary>The HTTPS address whose <c>Date</c> header is the independent clock reference (PLAN_windows_time_guard.md D2);
        /// empty turns the HTTP reference off. Root sends the request, so only the MACHINE layer may set it.</summary>
        public static readonly ConfigKey.TextKey ReferenceUrl = new("clock.referenceUrl", new TextRule.HttpsUrlOrEmpty())
        {
            Trust = new(SafeDirection.None, MachineOnly: true),
        };

        /// <summary>How far a clock may stand from the reference and still agree with it (D3). A smaller tolerance makes A16
        /// skip more, so lower is safer.</summary>
        public static readonly ConfigKey.IntKey ReferenceToleranceSeconds = new("clock.referenceToleranceSeconds", 5, 3600) { Trust = KeyTrust.Lower };

        /// <summary>Whether a Windows Time service that starts Manual is a warning (D4) — what the report SAYS; the extension's
        /// fix has its own switch for what it DOES.</summary>
        public static readonly ConfigKey.BoolKey ManualStartWarns = new("clock.manualStartWarns") { Trust = KeyTrust.Display };

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
