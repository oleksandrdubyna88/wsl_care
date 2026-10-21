namespace WslCare.Core.Config;

/// <summary>The <c>temp.*</c> keys (WR-T1 of PLAN_windows_runner.md §3.6, §3.12): what the Windows <c>%TEMP%</c> cleanup (A23) takes
/// and how far one run may go. In their own file, with the number keys joining <c>NumberKeys()</c> like every other number key.
/// None is machine-only: the action runs in the unelevated user's own process, so the user layer is the natural place for them
/// (the plan's Q2).</summary>
public static partial class ConfigKeys
{
    public static class Temp
    {
        /// <summary>At most this many members in <see cref="AllowGlobs"/> — a schema limit, part of the contract other tools write
        /// against (<c>contracts/config-keys.json</c>).</summary>
        public const int MaxAllowGlobs = 32;

        /// <summary>A top-level entry of <c>%TEMP%</c> is deleted only when its NEWEST descendant (files and folders) is older than this
        /// many days (§3.12). A user layer may only lengthen it. Default 7.</summary>
        public static readonly ConfigKey.IntKey OlderThanDays = new("temp.olderThanDays", 1, 365) { Trust = KeyTrust.Higher };

        /// <summary>The ALLOW-list (§3.12, the owner's decision of 2026-10-12): the name patterns whose top-level entries may be deleted.
        /// An OPEN list bound by <see cref="TextRule.TempGlobName"/> (no separator, never matching <c>claude</c> or the test-leftover
        /// prefix) and a cap. Default <c>coai-*</c>, <c>wsl-care-test-*</c>, <c>stage-*</c>, <c>mindex-*</c>, <c>git-commit-*</c>,
        /// <c>docrag-*</c>, <c>bge-seed-*</c>.</summary>
        public static readonly ConfigKey.TextListKey AllowGlobs = new("temp.allowGlobs", [], new TextRule.TempGlobName(), MaxAllowGlobs) { Trust = KeyTrust.Display };

        /// <summary>One run visits at most this many entries (candidates and everything inside them); a tree not scanned to its end
        /// inside it is "unknown — retained". Default 200000.</summary>
        public static readonly ConfigKey.IntKey ScanMaxNodes = new("temp.scanMaxNodes", 1000, 10_000_000) { Trust = KeyTrust.Lower };

        /// <summary>One run scans for at most this many seconds (the same ceiling, in time). Default 120.</summary>
        public static readonly ConfigKey.IntKey ScanMaxSeconds = new("temp.scanMaxSeconds", 10, 3600) { Trust = KeyTrust.Lower };

        /// <summary>One run deletes at most this many top-level entries; the rest wait for the next run (the cursor resumes). Default 5000.</summary>
        public static readonly ConfigKey.IntKey MaxItemsPerRun = new("temp.maxItemsPerRun", 1, 1_000_000) { Trust = KeyTrust.Lower };

        /// <summary>The most top-level entries of <c>%TEMP%</c> one run lists; a folder holding more is refused whole (a cut listing
        /// has no stable order for the cursor). Default 500000.</summary>
        public static readonly ConfigKey.IntKey MaxTopLevelEntries = new("temp.maxTopLevelEntries", 10_000, 10_000_000) { Trust = KeyTrust.Lower };
    }
}
