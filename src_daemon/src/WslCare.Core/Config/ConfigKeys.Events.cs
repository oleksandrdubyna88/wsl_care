namespace WslCare.Core.Config;

/// <summary>The <c>events.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key.</summary>
public static partial class ConfigKeys
{
    public static class Events
    {
        /// <summary>Container-start lines are kept this long. Default 14.</summary>
        public static readonly ConfigKey.IntKey StartsRetentionDays = new("events.startsRetentionDays", 1, 90) { Trust = new(SafeDirection.Higher, MachineOnly: true) };

        /// <summary>The first wait for the Docker socket. Default 5.</summary>
        public static readonly ConfigKey.IntKey RetryFirstSeconds = new("events.retryFirstSeconds", 1, 60) { Trust = KeyTrust.Higher };

        /// <summary>The longest wait for the Docker socket. Default 300.</summary>
        public static readonly ConfigKey.IntKey RetryMaxSeconds = new("events.retryMaxSeconds", 5, 3600) { Trust = KeyTrust.Higher };

        /// <summary>Each wait for the Docker socket is this many times the last. Default 2.</summary>
        public static readonly ConfigKey.IntKey RetryFactor = new("events.retryFactor", 1, 10) { Trust = KeyTrust.Higher };

        /// <summary>The follower counts as not running this long after its segment should have ended. Default 5.</summary>
        public static readonly ConfigKey.IntKey StalenessSlackMinutes = new("events.stalenessSlackMinutes", 1, 60) { Trust = KeyTrust.Display };

        /// <summary>How many images the starts summary names. Default 5.</summary>
        public static readonly ConfigKey.IntKey TopImages = new("events.topImages", 0, 50) { Trust = KeyTrust.Display };

        /// <summary>One docker events segment. Default 10.</summary>
        public static readonly ConfigKey.IntKey SegmentMinutes = new("events.segmentMinutes", 1, 50) { Trust = KeyTrust.Display };

        /// <summary>A segment's ceiling beyond its length. Default 60.</summary>
        public static readonly ConfigKey.IntKey SegmentSlackSeconds = new("events.segmentSlackSeconds", 10, 60) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>A segment that ends this much early is abnormal. Default 2.</summary>
        public static readonly ConfigKey.IntKey EarlyEndSeconds = new("events.earlyEndSeconds", 1, 10) { Trust = new(SafeDirection.None, MachineOnly: true) };
    }
}
