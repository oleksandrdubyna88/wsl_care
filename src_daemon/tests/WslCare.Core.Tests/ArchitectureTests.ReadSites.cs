using System.Text.RegularExpressions;

using FluentAssertions;

namespace WslCare.Core.Tests;

/// <summary>
/// Plan §15q R1.1 — the sweep left behind as a control. Root reading a file another party controls with the plain reader was a
/// CLASS (the user configuration layer and six more sites), so every read the product makes is classified here by WHOSE file
/// its path names, and a class whose files someone else controls must use its hardened reader. A new read site that is not in
/// this table fails, naming it; so does one whose class demands a reader it does not use.
/// </summary>
/// <remarks>The table is the enumeration, not a list kept in step by hand with a rule somewhere else: adding a read means
/// deciding its class HERE, in the same change. The companion test proves the scan still finds reads at all.</remarks>
public sealed partial class ArchitectureTests
{
    [GeneratedRegex(@"\.\s*(?<call>ReadFile|ReadRegularFile|ReadStateFile|ReadUserFile|ReadNoFollowFile|MeasureTree|WalkTree|ListFiles|ListDirectories|ListEntries)\s*\(|\bFile\s*\.\s*(?<call>ReadAll\w*|OpenRead|Open)\s*\(|\b(?<call>new\s+FileStream)\s*\(|\b(?<call>ProcText\s*\.\s*(?:Read|Bytes)|RegularFiles\s*\.\s*(?:Read|ReadHead|ReadOwned|ReadNoFollow)|BeneathFiles\s*\.\s*Read)\s*\(|(?<![\w.])(?<call>ReadText)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex ReadCall();

    /// <summary>Whose file a read names.</summary>
    internal enum ReadClass
    {
        /// <summary>The kernel's files (procfs, sysfs, cgroupfs, mountinfo, binfmt_misc) and root-owned <c>/etc</c> files.</summary>
        System,

        /// <summary>The daemon's own state and logs, written by root (<c>/var/lib/wsl-care</c>, <c>/var/log/wsl-care</c>).</summary>
        RootState,

        /// <summary>A state file another process will trust: owner-checked as root's (<c>ReadStateFile</c>).</summary>
        TrustedState,

        /// <summary>A configuration layer: the machine one as root's, the user one as its owner's.</summary>
        ConfigLayer,

        /// <summary>The target user's own file read for them (root working for that user): owner-checked, no link, no wait.</summary>
        TargetHome,

        /// <summary>A Windows-profile file through drvfs: no link, no wait, capped, no owner check (review M2).</summary>
        WindowsProfile,

        /// <summary>A file a person named on the command line, or the user's own layer rewritten by the user: regular, capped.</summary>
        Named,

        /// <summary>A file an UNPRIVILEGED run reads of its own account (an npm package's <c>package.json</c> for an agent's version,
        /// E7.S1) — the caller asks only when not root.</summary>
        OwnUnprivileged,

        /// <summary>Names and sizes under the target home — a walk that never follows a link, or a listing of names. Residual,
        /// stated: a listing of a folder that is itself a link lists through it (names only, nothing read).</summary>
        TargetHomeMetadata,
    }

    /// <summary>The calls each class may use — the hardened readers for every class someone else controls.</summary>
    private static readonly Dictionary<ReadClass, string[]> AllowedCalls = new()
    {
        [ReadClass.System] = ["ReadFile", "ReadAllText", "ListDirectories", "ListFiles", "ProcText.Read", "ProcText.Bytes", "ReadText", "RegularFiles.ReadHead"],
        [ReadClass.RootState] = ["ReadFile", "ListDirectories", "ListFiles", "new FileStream"],
        [ReadClass.TrustedState] = ["ReadStateFile"],
        [ReadClass.ConfigLayer] = ["ReadStateFile", "ReadUserFile"],
        [ReadClass.TargetHome] = ["ReadUserFile"],
        [ReadClass.WindowsProfile] = ["ReadNoFollowFile"],
        [ReadClass.Named] = ["ReadRegularFile"],
        [ReadClass.TargetHomeMetadata] = ["MeasureTree", "WalkTree", "ListDirectories", "ListFiles", "ListEntries"],
        [ReadClass.OwnUnprivileged] = ["ReadRegularFile", "ReadUserFile"],
    };

    /// <summary>Every read site of the product: file (relative to the source root) → call → (how many, whose file).</summary>
    internal static readonly Dictionary<string, Dictionary<string, (int Count, ReadClass Class)>> ReadSites = new()
    {
        ["WslCare.Cli/Commands/ActCommand.cs"] = new() { ["ReadRegularFile"] = (1, ReadClass.Named) },
        ["WslCare.Cli/Commands/RunStops.cs"] = new() { ["ReadFile"] = (1, ReadClass.System) },
        ["WslCare.Cli/Logging/DailyRunFileSink.cs"] = new() { ["new FileStream"] = (1, ReadClass.RootState) },
        ["WslCare.Cli/Logging/LogRetention.cs"] = new() { ["ListDirectories"] = (1, ReadClass.RootState) },
        ["WslCare.Core/Actions/Clock/ClockFix.cs"] = new() { ["ReadFile"] = (1, ReadClass.RootState) },
        ["WslCare.Core/Actions/Engine/DryRunWindow.cs"] = new() { ["ReadFile"] = (1, ReadClass.RootState) },
        ["WslCare.Core/Actions/Engine/ProcessTable.cs"] = new() { ["ReadAllText"] = (1, ReadClass.System), ["ReadText"] = (3, ReadClass.System) },
        ["WslCare.Core/Actions/Engine/RunRequests.cs"] = new() { ["ListFiles"] = (3, ReadClass.RootState), ["ReadStateFile"] = (1, ReadClass.TrustedState) },
        ["WslCare.Core/Actions/Engine/RunningState.cs"] = new() { ["ReadFile"] = (1, ReadClass.RootState) },
        ["WslCare.Core/Actions/Engine/StopMarkers.cs"] = new() { ["ListFiles"] = (1, ReadClass.RootState), ["ReadStateFile"] = (1, ReadClass.TrustedState) },
        ["WslCare.Core/Actions/JournalVacuum.cs"] = new() { ["ListDirectories"] = (1, ReadClass.System), ["ListFiles"] = (1, ReadClass.System) },
        ["WslCare.Core/Actions/TargetUserCommands.cs"] = new() { ["ListDirectories"] = (1, ReadClass.TargetHomeMetadata), ["ReadUserFile"] = (1, ReadClass.TargetHome) },
        ["WslCare.Core/Actions/TargetUser.cs"] = new() { ["ReadFile"] = (3, ReadClass.System) },
        ["WslCare.Core/Actions/UserCaches/BrowserAndHttpCaches.cs"] = new()
        {
            ["ListDirectories"] = (1, ReadClass.TargetHomeMetadata),
            ["ListFiles"] = (1, ReadClass.TargetHomeMetadata),
            ["ReadUserFile"] = (2, ReadClass.TargetHome),
        },
        ["WslCare.Core/Actions/UserCaches/CacheFolders.cs"] = new() { ["MeasureTree"] = (1, ReadClass.TargetHomeMetadata) },
        ["WslCare.Core/Actions/UserCaches/EditorServerCleanup.cs"] = new() { ["ListDirectories"] = (2, ReadClass.TargetHomeMetadata), ["ReadUserFile"] = (1, ReadClass.TargetHome) },
        ["WslCare.Core/Collectors/ContainerCgroups.cs"] = new() { ["ListDirectories"] = (2, ReadClass.System), ["ProcText.Read"] = (2, ReadClass.System) },
        ["WslCare.Core/Mcp/McpServerCollector.cs"] = new() { ["ProcText.Bytes"] = (1, ReadClass.System) },
        ["WslCare.Core/Mcp/McpRunLogs.cs"] = new() { ["ListEntries"] = (1, ReadClass.TargetHomeMetadata) },
        // Plan E14 S1: the MCP CPU ledger — root's, read as root's state; an unprivileged status's own, owner-checked with no link
        // (review finding 2: ReadStateFile trusts root's files only).
        // The orphan sweep (coai plan round finding 3) lists the ledger's own folder — names only, nothing opened, in either
        // place (root's state folder or this account's), so the stricter metadata class holds for both.
        ["WslCare.Core/Mcp/McpCpuLedger.cs"] = new() { ["ReadStateFile"] = (1, ReadClass.TrustedState), ["ReadUserFile"] = (1, ReadClass.OwnUnprivileged), ["ListFiles"] = (1, ReadClass.TargetHomeMetadata) },
        ["WslCare.Core/Collectors/Procfs/SampleTime.cs"] = new() { ["ProcText.Read"] = (1, ReadClass.System) },
        // E14 S6: /proc/loadavg for `wsl-care busy` — the kernel's file.
        ["WslCare.Core/Collectors/Procfs/LoadAverageFile.cs"] = new() { ["ProcText.Read"] = (1, ReadClass.System) },
        ["WslCare.Core/Collectors/ProcessCollector.cs"] = new() { ["ListDirectories"] = (1, ReadClass.System), ["ProcText.Read"] = (5, ReadClass.System), ["ProcText.Bytes"] = (1, ReadClass.System) },
        ["WslCare.Core/Collectors/Procfs/ProcText.cs"] = new() { ["ReadFile"] = (1, ReadClass.System) },
        ["WslCare.Core/Config/ConfigLoader.cs"] = new() { ["ReadStateFile"] = (1, ReadClass.ConfigLayer), ["ReadUserFile"] = (1, ReadClass.ConfigLayer) },
        ["WslCare.Core/Config/UserConfigWriter.cs"] = new() { ["ReadRegularFile"] = (1, ReadClass.Named) },
        ["WslCare.Core/Docker/DockerHygiene.cs"] = new() { ["ReadNoFollowFile"] = (1, ReadClass.WindowsProfile) },
        ["WslCare.Core/Docker/VolumeSeen.cs"] = new() { ["ReadFile"] = (1, ReadClass.RootState) },
        ["WslCare.Core/Events/ContainerStartsStore.cs"] = new() { ["ListFiles"] = (3, ReadClass.RootState), ["ReadFile"] = (2, ReadClass.RootState) },
        ["WslCare.Core/Folders/FolderSizes.cs"] = new() { ["MeasureTree"] = (1, ReadClass.TargetHomeMetadata) },
        ["WslCare.Core/Health/HealthCollector.cs"] = new() { ["ListFiles"] = (1, ReadClass.System), ["ReadNoFollowFile"] = (1, ReadClass.WindowsProfile), ["ProcText.Read"] = (2, ReadClass.System) },
        ["WslCare.Core/History/RunLogs.cs"] = new() { ["ReadFile"] = (1, ReadClass.RootState) },
        ["WslCare.Core/History/RunShow.cs"] = new() { ["ReadFile"] = (1, ReadClass.RootState) },
        ["WslCare.Core/Archive/BaseFolderRules.cs"] = new() { ["ProcText.Read"] = (1, ReadClass.System) },
        ["WslCare.Core/Processes/WindowsSystemDrive.cs"] = new() { ["ReadAllText"] = (1, ReadClass.System), ["ReadText"] = (3, ReadClass.System) },
        ["WslCare.Core/Records/RunDetailStore.cs"] = new() { ["ListDirectories"] = (1, ReadClass.RootState), ["ListFiles"] = (1, ReadClass.RootState), ["ReadFile"] = (1, ReadClass.RootState) },
        ["WslCare.Core/Records/RunHistory.cs"] = new() { ["ReadFile"] = (1, ReadClass.RootState) },
        ["WslCare.Core/Records/RunRetention.cs"] = new() { ["ListDirectories"] = (2, ReadClass.RootState), ["ListFiles"] = (3, ReadClass.RootState) },
        ["WslCare.Core/Status/FullRunVerdicts.cs"] = new() { ["ReadFile"] = (1, ReadClass.RootState) },
        // E7.S1: the agents — walks and listings of their folders (names and sizes, nothing opened), and a package.json read
        // only when not root.
        ["WslCare.Core/Agents/AgentDiscovery.cs"] = new() { ["ReadRegularFile"] = (1, ReadClass.OwnUnprivileged), ["ListEntries"] = (1, ReadClass.TargetHomeMetadata) },
        ["WslCare.Core/Agents/AgentWalk.cs"] = new() { ["WalkTree"] = (2, ReadClass.TargetHomeMetadata) },
        ["WslCare.Core/Agents/SessionGlob.cs"] = new() { ["ListEntries"] = (1, ReadClass.TargetHomeMetadata) },

        // Review S2: the reads through a wrapper — procfs, /etc, binfmt_misc, the drive's powershell.exe head — all the kernel's or root's.
        ["WslCare.Core/Actions/Disk/FilesystemTrim.cs"] = new() { ["ProcText.Read"] = (1, ReadClass.System) },
        ["WslCare.Core/Actions/Engine/IdleGate.cs"] = new() { ["ProcText.Read"] = (2, ReadClass.System) },
        ["WslCare.Core/Actions/Memory/MemoryNow.cs"] = new() { ["ProcText.Bytes"] = (1, ReadClass.System) },
        ["WslCare.Core/Collectors/Procfs/PidSamples.cs"] = new() { ["ProcText.Read"] = (2, ReadClass.System) },
        ["WslCare.Core/Actions/Suspects/AgentOrphans.cs"] = new() { ["ProcText.Read"] = (1, ReadClass.System) },
        ["WslCare.Core/Actions/Suspects/AgentCpuHistory.cs"] = new() { ["ReadStateFile"] = (1, ReadClass.TrustedState) },
        // Plan E14 S2b: the watch's tries — root's state, read back as such.
        ["WslCare.Core/Watch/WatchRun.cs"] = new() { ["ReadStateFile"] = (1, ReadClass.TrustedState) },
        ["WslCare.Core/Collectors/LinuxProbe.cs"] = new() { ["ProcText.Bytes"] = (1, ReadClass.System) },
        ["WslCare.Core/Collectors/MemoryCollector.cs"] = new() { ["ProcText.Read"] = (2, ReadClass.System) },
        // E14 S6: /proc/pressure/{memory,io,cpu} — moved out of the memory collector so `wsl-care busy` reads it alone.
        ["WslCare.Core/Collectors/Procfs/Pressure.cs"] = new() { ["ProcText.Read"] = (1, ReadClass.System) },
        ["WslCare.Core/Config/UserLayerTrusts.cs"] = new() { ["ProcText.Read"] = (1, ReadClass.System) },
        ["WslCare.Core/Doctor/DoctorRun.cs"] = new() { ["ProcText.Read"] = (1, ReadClass.System), ["ReadFile"] = (1, ReadClass.System) },
        ["WslCare.Core/Health/WindowsProfiles.cs"] = new() { ["ProcText.Read"] = (1, ReadClass.System) },
        ["WslCare.Core/Processes/ProcessSignals.cs"] = new() { ["ProcText.Read"] = (1, ReadClass.System) },
        ["WslCare.Core/Processes/SystemDriveFiles.cs"] = new() { ["RegularFiles.ReadHead"] = (1, ReadClass.System) },
    };

    /// <summary>The file system's own implementation files — the readers themselves, not their callers.</summary>
    private static readonly string[] ReaderImplementations =
        ["WslCare.Core/Files/PhysicalFileSystem.cs", "WslCare.Core/Files/IFileSystem.cs", "WslCare.Core/Files/RegularFiles.cs", "WslCare.Core/Files/TreeWalk.cs", "WslCare.Core/Files/BeneathFiles.cs"];

    /// <summary>Every read call in <paramref name="source"/>, normalised (<c>File.ReadAllText</c> → <c>ReadAllText</c>).</summary>
    internal static IReadOnlyList<string> ReadCalls(string source) =>
        [.. ReadCall().Matches(source).Select(m => Regex.Replace(Regex.Replace(m.Groups["call"].Value, @"\s*\.\s*", "."), @"\s+", " "))];

    /// <summary>Review S2: a read through a WRAPPER — <c>ProcText.Read</c> / <c>Bytes</c>, <c>RegularFiles.Read</c> /
    /// <c>ReadHead</c>, <c>WindowsSystemDrive</c>'s <c>ReadText</c> — is a read like any other and must be classified too.</summary>
    [Fact]
    public void The_read_scan_finds_a_read_through_a_wrapper()
    {
        ReadCalls("var a = ProcText.Read(files, home + \"/.bashrc\");\nvar b = ProcText\n    .Bytes(files, p);\nvar c = RegularFiles.Read(p, 64); var d = RegularFiles.ReadHead(p, 2); var e = ReadText(p);")
            .Should().Equal("ProcText.Read", "ProcText.Bytes", "RegularFiles.Read", "RegularFiles.ReadHead", "ReadText");
    }

    [Fact]
    public void Every_read_is_classified_by_whose_file_it_names_and_uses_that_class_s_reader()
    {
        var root = Metadata("WslCare.SourceRoot");
        var found = SourceFiles()
            .Select(file => (Relative: Path.GetRelativePath(root, file).Replace('\\', '/'), Calls: ReadCalls(File.ReadAllText(file))))
            .Where(f => f.Calls.Count > 0 && !ReaderImplementations.Contains(f.Relative, StringComparer.Ordinal))
            .ToDictionary(f => f.Relative, f => f.Calls.GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count()), StringComparer.Ordinal);

        var unclassified = found.SelectMany(f => f.Value.Select(c => (f.Key, c.Key, c.Value)))
            .Where(s => !ReadSites.TryGetValue(s.Item1, out var calls) || !calls.TryGetValue(s.Item2, out var site) || site.Count != s.Item3)
            .Select(s => $"{s.Item1}: {s.Item3} × {s.Item2}");
        var stale = ReadSites.SelectMany(f => f.Value.Select(c => (f.Key, c.Key)))
            .Where(s => !found.TryGetValue(s.Item1, out var calls) || !calls.ContainsKey(s.Item2))
            .Select(s => $"{s.Item1}: {s.Item2} (in the table, not in the source)");
        var wrongReader = ReadSites.SelectMany(f => f.Value.Select(c => (f.Key, c.Key, c.Value.Class)))
            .Where(s => !AllowedCalls[s.Class].Contains(s.Item2, StringComparer.Ordinal))
            .Select(s => $"{s.Item1}: {s.Item2} is not a reader of class {s.Class}");

        unclassified.Concat(stale).Concat(wrongReader).Should().BeEmpty(
            "every read is decided here: whose file it names, and a file someone else controls goes through its hardened reader (plan §15q R1.1)");
    }

    [Fact]
    public void The_read_scan_finds_a_planted_read_across_lines_and_the_known_hardened_ones()
    {
        ReadCalls("var a = files\n    .ReadFile (\n        path);\nvar b = File.ReadAllBytes(p); var c = new FileStream(p, o);")
            .Should().Equal("ReadFile", "ReadAllBytes", "new FileStream");
        var loader = Path.Combine(Metadata("WslCare.SourceRoot"), "WslCare.Core", "Config", "ConfigLoader.cs");
        ReadCalls(File.ReadAllText(loader)).Should().Contain(["ReadStateFile", "ReadUserFile"]);
    }
}
