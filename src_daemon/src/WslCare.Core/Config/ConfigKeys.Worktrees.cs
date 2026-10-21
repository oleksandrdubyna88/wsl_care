namespace WslCare.Core.Config;

/// <summary>The <c>worktrees.*</c> keys (E19.S3 of PLAN_disk_hygiene_windows.md §3.9): which folders the worktree report looks at and the
/// numbers it judges them by. One file per key group, as the others.</summary>
public static partial class ConfigKeys
{
    public static class Worktrees
    {
        /// <summary>At most this many closed-list members of <see cref="BuildArtifactPatterns"/> — a schema limit, part of the contract
        /// other tools write against (<c>contracts/config-keys.json</c>).</summary>
        public const int MaxArtifactPatterns = 128;

        /// <summary>At most this many members of <see cref="WslAutomountRoots"/> — a schema limit.</summary>
        public const int MaxAutomountRoots = 8;

        /// <summary>The most roots a list may hold. The administrator's: a root names where a later cleanup is admitted to delete.</summary>
        public static readonly ConfigKey.IntKey MaxRoots = new("worktrees.maxRoots", 1, 32) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The folders the report looks at, each the PARENT of worktrees — default none, never a hard-coded user path. An ordinary
        /// USER key (E19 stories §3 decision 3): the user-run <c>worktrees roots set</c> writes it, so the machine layer cannot be where it
        /// lives; its safety is the root rules (<c>Worktrees.WorktreeRoots</c>), judged at <c>roots set</c> AND again at the start of every
        /// scan — a hand-edited bad root is refused where it is used, and an invalid value in the layer is a notice, never an error.</summary>
        public static readonly ConfigKey.TextListKey Roots = new("worktrees.roots", [], new TextRule.WindowsLocalPath(), MaxRoots.Max) { Trust = KeyTrust.Display };

        /// <summary>A folder untouched for this many days is idle. A shorter idle loosens what a cleanup may delete: the user layer only raises it.</summary>
        public static readonly ConfigKey.IntKey IdleDays = new("worktrees.idleDays", 1, 365) { Trust = KeyTrust.Higher };

        /// <summary>The project files that make a <c>bin</c>/<c>obj</c> beside them build output — a CLOSED set: a new extension widens the rule.</summary>
        public static readonly ConfigKey.TextListKey ProjectFileExtensions = new("worktrees.projectFileExtensions", [".csproj", ".fsproj", ".vbproj", ".esproj", ".sqlproj", ".vcxproj"]) { Trust = new(SafeDirection.Subset) };

        /// <summary>The file kinds a build writes (the M6 census of 321 real worktrees): an extension (<c>.dll</c>), <c>none</c> for a file
        /// with no extension (native runtime executables), or <c>obj:.cs</c> for an extension that counts only under <c>obj</c> (generated
        /// sources). A bin/obj holding any file outside it is kept whole and the file named. The administrator's: a wider list loosens what is deleted.</summary>
        public static readonly ConfigKey.TextListKey BuildArtifactPatterns = new(
            "worktrees.buildArtifactPatterns",
            [],
            new TextRule.Matching(@"^(none|(obj:)?\.[A-Za-z0-9_-]{1,16})$", "none, an extension such as .dll, or obj: and an extension (obj:.cs)"),
            MaxArtifactPatterns)
        { Trust = new(SafeDirection.Subset, MachineOnly: true) };

        /// <summary>A file written more than this many minutes after the newest compiled output of its folder is the application's, not
        /// the build's: it keeps its bin/obj (E19 stories §3 decision 11). A larger skew admits more files as artifacts: the user layer only lowers it.</summary>
        public static readonly ConfigKey.IntKey ArtifactSkewMinutes = new("worktrees.artifactSkewMinutes", 1, 1440) { Trust = KeyTrust.Lower };

        /// <summary>The distro's automount prefixes a WSL-made worktree's pointer is translated through (the Windows side cannot read
        /// <c>/etc/wsl.conf</c>); a POSIX pointer under none of them is foreign and never a candidate. The administrator's: a wrong
        /// entry would let a live worktree read as an orphan.</summary>
        public static readonly ConfigKey.TextListKey WslAutomountRoots = new(
            "worktrees.wslAutomountRoots",
            [],
            new TextRule.Matching(@"^/([A-Za-z0-9._-]+/)+$", "an absolute POSIX folder ending in / (such as /mnt/)"),
            MaxAutomountRoots)
        { Trust = new(SafeDirection.Subset, MachineOnly: true) };

        /// <summary>The whole scan's entry budget — a walk that runs out leaves the rest unknown, never idle. The user layer only lowers it.</summary>
        public static readonly ConfigKey.IntKey ScanMaxEntries = new("worktrees.scanMaxEntries", 10_000, 50_000_000) { Trust = KeyTrust.Lower };

        /// <summary>The whole scan's time budget. The user layer only lowers it.</summary>
        public static readonly ConfigKey.IntKey ScanMaxSeconds = new("worktrees.scanMaxSeconds", 60, 7200) { Trust = KeyTrust.Lower };

        /// <summary>How many of the largest folders (and named retentions) the report lists.</summary>
        public static readonly ConfigKey.IntKey ReportTop = new("worktrees.reportTop", 1, 200) { Trust = KeyTrust.Display };

        /// <summary>A report older than this many days is stale: the status says so.</summary>
        public static readonly ConfigKey.IntKey ReportMaxAgeDays = new("worktrees.reportMaxAgeDays", 1, 90) { Trust = KeyTrust.Display };

        /// <summary>The reclaimable total (git-confirmed, E19.S4) above which the verdict warns.</summary>
        public static readonly ConfigKey.IntKey ReclaimableWarnGib = new("worktrees.reclaimableWarnGib", 1, 10240) { Trust = KeyTrust.Display };
    }
}
