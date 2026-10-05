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
    [GeneratedRegex(@"\.\s*(?<call>ReadFile|ReadRegularFile|ReadStateFile|ReadUserFile|ReadNoFollowFile|MeasureTree|ListFiles|ListDirectories)\s*\(|\bFile\s*\.\s*(?<call>ReadAll\w*|OpenRead|Open)\s*\(|\b(?<call>new\s+FileStream)\s*\(", RegexOptions.CultureInvariant)]
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

        /// <summary>Names and sizes under the target home — a walk that never follows a link, or a listing of names. Residual,
        /// stated: a listing of a folder that is itself a link lists through it (names only, nothing read).</summary>
        TargetHomeMetadata,
    }

    /// <summary>The calls each class may use — the hardened readers for every class someone else controls.</summary>
    private static readonly Dictionary<ReadClass, string[]> AllowedCalls = new()
    {
        [ReadClass.System] = ["ReadFile", "ReadAllText", "ListDirectories", "ListFiles"],
        [ReadClass.RootState] = ["ReadFile", "ListDirectories", "ListFiles", "new FileStream"],
        [ReadClass.TrustedState] = ["ReadStateFile"],
        [ReadClass.ConfigLayer] = ["ReadStateFile", "ReadUserFile"],
        [ReadClass.TargetHome] = ["ReadUserFile"],
        [ReadClass.WindowsProfile] = ["ReadNoFollowFile"],
        [ReadClass.Named] = ["ReadRegularFile"],
        [ReadClass.TargetHomeMetadata] = ["MeasureTree", "ListDirectories", "ListFiles"],
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
        ["WslCare.Core/Actions/Engine/ProcessTable.cs"] = new() { ["ReadAllText"] = (1, ReadClass.System) },
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
        ["WslCare.Core/Collectors/ContainerCgroups.cs"] = new() { ["ListDirectories"] = (2, ReadClass.System) },
        ["WslCare.Core/Collectors/ProcessCollector.cs"] = new() { ["ListDirectories"] = (1, ReadClass.System) },
        ["WslCare.Core/Collectors/Procfs/ProcText.cs"] = new() { ["ReadFile"] = (1, ReadClass.System) },
        ["WslCare.Core/Config/ConfigLoader.cs"] = new() { ["ReadStateFile"] = (1, ReadClass.ConfigLayer), ["ReadUserFile"] = (1, ReadClass.ConfigLayer) },
        ["WslCare.Core/Config/UserConfigWriter.cs"] = new() { ["ReadRegularFile"] = (1, ReadClass.Named) },
        ["WslCare.Core/Docker/DockerHygiene.cs"] = new() { ["ReadNoFollowFile"] = (1, ReadClass.WindowsProfile) },
        ["WslCare.Core/Docker/VolumeSeen.cs"] = new() { ["ReadFile"] = (1, ReadClass.RootState) },
        ["WslCare.Core/Events/ContainerStartsStore.cs"] = new() { ["ListFiles"] = (2, ReadClass.RootState), ["ReadFile"] = (2, ReadClass.RootState) },
        ["WslCare.Core/Folders/FolderSizes.cs"] = new() { ["MeasureTree"] = (1, ReadClass.TargetHomeMetadata) },
        ["WslCare.Core/Health/HealthCollector.cs"] = new() { ["ListFiles"] = (1, ReadClass.System), ["ReadNoFollowFile"] = (1, ReadClass.WindowsProfile) },
        ["WslCare.Core/History/RunLogs.cs"] = new() { ["ReadFile"] = (1, ReadClass.RootState) },
        ["WslCare.Core/History/RunShow.cs"] = new() { ["ReadFile"] = (1, ReadClass.RootState) },
        ["WslCare.Core/Processes/WindowsSystemDrive.cs"] = new() { ["ReadAllText"] = (1, ReadClass.System) },
        ["WslCare.Core/Records/RunDetailStore.cs"] = new() { ["ListDirectories"] = (1, ReadClass.RootState), ["ListFiles"] = (1, ReadClass.RootState), ["ReadFile"] = (1, ReadClass.RootState) },
        ["WslCare.Core/Records/RunHistory.cs"] = new() { ["ReadFile"] = (1, ReadClass.RootState) },
        ["WslCare.Core/Records/RunRetention.cs"] = new() { ["ListDirectories"] = (2, ReadClass.RootState), ["ListFiles"] = (3, ReadClass.RootState) },
        ["WslCare.Core/Status/FullRunVerdicts.cs"] = new() { ["ReadFile"] = (1, ReadClass.RootState) },
    };

    /// <summary>The file system's own implementation files — the readers themselves, not their callers.</summary>
    private static readonly string[] ReaderImplementations =
        ["WslCare.Core/Files/PhysicalFileSystem.cs", "WslCare.Core/Files/IFileSystem.cs", "WslCare.Core/Files/RegularFiles.cs", "WslCare.Core/Files/TreeWalk.cs"];

    /// <summary>Every read call in <paramref name="source"/>, normalised (<c>File.ReadAllText</c> → <c>ReadAllText</c>).</summary>
    internal static IReadOnlyList<string> ReadCalls(string source) =>
        [.. ReadCall().Matches(source).Select(m => Regex.Replace(m.Groups["call"].Value, @"\s+", " "))];

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
