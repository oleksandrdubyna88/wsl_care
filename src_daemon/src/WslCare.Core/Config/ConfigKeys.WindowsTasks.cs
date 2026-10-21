namespace WslCare.Core.Config;

/// <summary>The <c>windowsTasks.*</c> keys (WR-D1 of PLAN_windows_runner.md §3.6): the numbers of the two per-user scheduled tasks
/// the Windows binary renders with <c>tasks render</c> — in their own file so <c>ConfigKeys.Numbers.cs</c> does not grow (the
/// doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other number key. None is machine-only: an
/// unelevated runner's own user layer is the natural place for them (the plan's Q2).</summary>
public static partial class ConfigKeys
{
    public static class WindowsTasks
    {
        /// <summary>The <c>\wsl-care\watch</c> task starts this many seconds after the person logs on, then repeats every
        /// <c>mcpWatchdog.periodMinutes</c>. A longer delay only starts the sampling later, so a user layer may only lengthen it.
        /// Default 60.</summary>
        public static readonly ConfigKey.IntKey WatchLogonDelaySeconds = new("windowsTasks.watchLogonDelaySeconds", 0, 3600) { Trust = KeyTrust.Higher };

        /// <summary>The <c>\wsl-care\archive</c> task starts this many minutes after the person logs on (the Windows plan's E11:
        /// "logon + 20 min"). Default 20.</summary>
        public static readonly ConfigKey.IntKey ArchiveLogonDelayMinutes = new("windowsTasks.archiveLogonDelayMinutes", 0, 720) { Trust = KeyTrust.Higher };

        /// <summary>The <c>\wsl-care\archive</c> task repeats every this many hours after its logon start. Default 4.</summary>
        public static readonly ConfigKey.IntKey ArchiveEveryHours = new("windowsTasks.archiveEveryHours", 1, 24) { Trust = KeyTrust.Higher };

        /// <summary>The <c>\wsl-care\cleanup</c> task (WR-T2, the Windows <c>%TEMP%</c> cleanup, A23) starts this many minutes after the
        /// person logs on. Default 30.</summary>
        public static readonly ConfigKey.IntKey CleanupLogonDelayMinutes = new("windowsTasks.cleanupLogonDelayMinutes", 0, 720) { Trust = KeyTrust.Higher };

        /// <summary>The <c>\wsl-care\cleanup</c> task repeats every this many hours after its logon start. Default 24.</summary>
        public static readonly ConfigKey.IntKey CleanupEveryHours = new("windowsTasks.cleanupEveryHours", 1, 168) { Trust = KeyTrust.Higher };
    }
}
