using WslCare.Core.Config;
using System.Collections;
using System.Diagnostics;
using System.IO.Enumeration;

namespace WslCare.Core.Files;

/// <summary>
/// What a bounded walk may enter (plan §15q R2.3): files counted only below folders of <see cref="CountOnlyUnder"/> (empty =
/// all), folders of the names in <see cref="NeverEnter"/> and of the prefixes in <see cref="NeverEnterPrefixes"/> not entered
/// at all — not even a stat inside — and, with <see cref="StayOnDevice"/>, no folder on another filesystem than the root's
/// (review C1: a nested bind mount must not drag the walk onto 9p). Every exclusion is named in the measure.
/// </summary>
public sealed record TreeRules(IReadOnlySet<string> CountOnlyUnder, IReadOnlySet<string> NeverEnter)
{
    /// <summary>Folders whose name starts with one of these are not entered (Gemini CLI's tree holds Antigravity's <c>antigravity*</c>).</summary>
    public IReadOnlyList<string> NeverEnterPrefixes { get; init; } = [];

    /// <summary>A folder on another device than the root's is not entered, and named.</summary>
    public bool StayOnDevice { get; init; }

    /// <summary>The walk also LISTS every file it counts — its full path, length and last write, from the directory entry alone
    /// (plan §15r E9.S1: one session moves under the month of its NEWEST file, companions included). No file is opened.</summary>
    public bool ListFiles { get; init; }

    /// <summary>The walk COUNTS every entry it could not open instead of skipping it silently (E19.S3: a skipped, unreadable subtree
    /// would hide a fresh write and let a worktree read idle) and reports the count on the measure. Off — today's behaviour — for every
    /// other caller.</summary>
    public bool CountDenied { get; init; }

    /// <summary>A folder this says <c>true</c> of — its full path and its name — is not entered, and the walk does not count it among the
    /// exclusions: the caller that asked keeps its own record of what it skipped. Its own last write is still seen. Never entered by default.</summary>
    public Func<string, string, bool> SkipDirectory { get; init; } = static (_, _) => false;

    /// <summary>Names (compared with the set's own comparer) whose entries, files or folders, the walk reports on the measure.</summary>
    public IReadOnlySet<string> ReportNames { get; init; } = NoNames;

    /// <summary>The most paths <see cref="ReportNames"/> reports — a bound on the answer, not on the walk.</summary>
    public int ReportNamesMax { get; init; } = 1;

    /// <summary>The measure carries the newest last write the walk saw.</summary>
    public bool TrackNewest { get; init; }

    private static readonly IReadOnlySet<string> NoNames = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Everything counted, everything entered.</summary>
    public static TreeRules All { get; } = new(NoNames, NoNames);
}

/// <summary>
/// The bounded walk behind <see cref="IFileSystem.MeasureTree"/> and <see cref="IFileSystem.WalkTree"/>: one
/// <see cref="FileSystemEnumerable{TResult}"/> pass, a stat per entry and no read, links neither counted nor entered
/// (<see cref="FileAttributes.ReparsePoint"/> is skipped — on Linux .NET reports a symlink with that attribute, on Windows a
/// symlink or a junction), stopped at the entry count or the deadline of <see cref="TreeLimits"/>, whichever comes first.
/// </summary>
/// <remarks>Why links are never followed: a walk of <c>~/.cache</c> or of <c>~/git</c> must not leave the tree it
/// was asked about — through a link into <c>/mnt/c</c> it would become the 9p walk that failed on 2026-10-01
/// (plan §2), and through a link into an AI agent's folder it would measure what is not its business (plan §4.6
/// measures those folders itself).</remarks>
internal static class TreeWalk
{
    private const int CancellationStride = 1024;

    /// <summary>At most this many exclusions are named; past it the measure says how many more there were.</summary>
    internal static int MaxExclusionsNamed => Tuning.Current.Int(ConfigKeys.Walk.MaxExclusionsNamed);

    public static TreeMeasure Measure(string root, TreeLimits limits, IReadOnlySet<string> countOnlyUnder, IReadOnlySet<string> neverEnter, CancellationToken cancellationToken) =>
        Measure(root, limits, new TreeRules(countOnlyUnder, neverEnter), DeviceOf, cancellationToken);

    /// <summary>The walk under <paramref name="rules"/>; <paramref name="deviceOf"/> answers a folder's device (never through a
    /// link), or <c>null</c> when it cannot be read — a seam, so the per-directory device rule is a unit test.</summary>
    public static TreeMeasure Measure(string root, TreeLimits limits, TreeRules rules, Func<string, (uint Major, uint Minor)?> deviceOf, CancellationToken cancellationToken)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(root);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new TreeMeasure.Missing();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new TreeMeasure.Unreadable($"{root} could not be inspected: {e.Message}");
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return new TreeMeasure.Unreadable($"{root} is a link; a walk never follows one");
        }

        return !attributes.HasFlag(FileAttributes.Directory) ? new TreeMeasure.Unreadable($"{root} is a file, not a folder")
            : IsNeverEntered(rules, Path.GetFileName(Path.TrimEndingDirectorySeparator(root))) is { Length: > 0 } never ? new TreeMeasure.Unreadable($"{root} is a {never} folder, never entered (plan §15q H2)")
            : WalkOnDevice(root, limits, rules, deviceOf, cancellationToken);
    }

    /// <summary>The rule a folder of this name falls under — <c>memory</c>, a prefix — or empty (review S2: the ROOT itself too).</summary>
    private static string IsNeverEntered(TreeRules rules, string name) =>
        rules.NeverEnter.Contains(name) ? $"\"{name}\""
        : rules.NeverEnterPrefixes.FirstOrDefault(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) is { } prefix ? $"\"{prefix}*\""
        : string.Empty;

    /// <summary>The device the walk must stay on, read first; a root whose device cannot be read is not walked when the rules
    /// ask to stay on it (a guarantee that cannot be checked is not given).</summary>
    private static TreeMeasure WalkOnDevice(string root, TreeLimits limits, TreeRules rules, Func<string, (uint, uint)?> deviceOf, CancellationToken cancellationToken)
    {
        var device = rules.StayOnDevice ? deviceOf(root) : null;
        return rules.StayOnDevice && device is null
            ? new TreeMeasure.Unreadable($"{root}: its device could not be read, so a walk that must stay on it is not started")
            : Walk(root, limits, rules, new Exclusions(root, device, deviceOf), cancellationToken);
    }

    /// <summary>This machine's device of a folder: <c>statx</c> without following a link on Linux; one device for every
    /// folder elsewhere (a Windows junction is a reparse point, never entered anyway).</summary>
    internal static (uint Major, uint Minor)? DeviceOf(string path) =>
        OperatingSystem.IsLinux()
            ? RegularFiles.StatNoFollow(path) is Collectors.Reading<FileStatus>.Available { Value: var s } ? (s.DeviceMajor, s.DeviceMinor) : null
            : (0u, 0u);

    private static TreeMeasure Walk(string root, TreeLimits limits, TreeRules rules, Exclusions excluded, CancellationToken cancellationToken)
    {
        var state = new WalkState(Stopwatch.StartNew(), limits);
        var probe = new Probe(rules);
        return Consume(Entries(root, rules, state, excluded, probe), rules, state, excluded, probe, cancellationToken);
    }

    /// <summary>The entries of the walk: today's ignoring enumeration for every caller that did not ask to count what it could not open,
    /// the counting one for the caller that did (<see cref="TreeRules.CountDenied"/>).</summary>
    private static IEnumerable<(long Length, bool Counted, TreeFile? File)> Entries(string root, TreeRules rules, WalkState state, Exclusions excluded, Probe probe)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = !rules.CountDenied,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };
        return NeedsTheCountingWalk(rules)
            ? new CountingEntries(root, options, rules, state, excluded, probe)
            : new FileSystemEnumerable<(long Length, bool Counted, TreeFile? File)>(
                root,
                (ref FileSystemEntry e) => (e.Length, rules.CountOnlyUnder.Count == 0 || IsUnderNamed(root, e.Directory.ToString(), rules.CountOnlyUnder), rules.ListFiles ? new TreeFile(e.ToFullPath(), e.Length, e.LastWriteTimeUtc) : null),
                options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
                ShouldRecursePredicate = (ref FileSystemEntry e) => !state.Stopped && excluded.MayEnter(rules, e.FileName.ToString(), e.ToFullPath()),
            };
    }

    /// <summary>The walks that count, track or report something ride the subclass; every other walk keeps today's delegate enumerable.</summary>
    private static bool NeedsTheCountingWalk(TreeRules rules) => rules.CountDenied || rules.TrackNewest || rules.ReportNames.Count > 0;

    private static TreeMeasure Consume(IEnumerable<(long Length, bool Counted, TreeFile? File)> entries, TreeRules rules, WalkState state, Exclusions excluded, Probe probe, CancellationToken cancellationToken)
    {
        long bytes = 0;
        long files = 0;
        long visited = 0;
        var listed = new List<TreeFile>();
        foreach (var (length, counted, file) in entries)
        {
            visited++;
            if (counted)
            {
                bytes += length;
                files++;
                Keep(listed, file);
            }

            if (visited % CancellationStride == 0)
            {
                // A stretch of a walk is a step of the run (E7.S2b/S2c review C-H2).
                Actions.Engine.RunProgress.Mark();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (state.Exhausted(visited) is { Length: > 0 } why)
            {
                state.Stopped = true;
                return Measured(bytes, files, false, why, rules, excluded, listed, probe);
            }
        }

        return Measured(bytes, files, true, string.Empty, rules, excluded, listed, probe);
    }

    private static TreeMeasure.Measured Measured(long bytes, long files, bool complete, string note, TreeRules rules, Exclusions excluded, List<TreeFile> listed, Probe probe) =>
        new(bytes, files, complete, note)
        {
            Excluded = excluded.Named,
            Listed = rules.ListFiles ? listed : Array.Empty<TreeFile>(),
            Denied = probe.Denied,
            Found = probe.Found,
            NewestWriteUtc = probe.Newest,
        };

    private static void Keep(List<TreeFile> listed, TreeFile? file)
    {
        if (file is not null)
        {
            listed.Add(file);
        }
    }

    /// <summary>Whether a file in <paramref name="directory"/> lies below a folder of one of <paramref name="names"/>, counted from <paramref name="root"/>.</summary>
    private static bool IsUnderNamed(string root, string directory, IReadOnlySet<string> names) =>
        directory.Length > root.Length
        && directory[root.Length..].Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(names.Contains);

    /// <summary>What the walk declined to enter, each named once: by name, by prefix, or as another filesystem.</summary>
    private sealed class Exclusions(string root, (uint, uint)? device, Func<string, (uint, uint)?> deviceOf)
    {
        private readonly List<string> _named = [];
        private int _more;

        public IReadOnlyList<string> Named => _named.Count == 0 ? Array.Empty<string>() : _more == 0 ? [.. _named] : [.. _named, $"{_more} more not named"];

        public bool MayEnter(TreeRules rules, string name, string fullPath) => Reason(rules, name, fullPath) is not { } why || Exclude(why);

        private string? Reason(TreeRules rules, string name, string fullPath) =>
            rules.NeverEnter.Contains(name) ? $"{name} (never entered)"
            : rules.NeverEnterPrefixes.FirstOrDefault(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) is { } prefix ? $"{prefix}* (never entered)"
            : OnAnotherDevice(fullPath) ? $"{Path.GetRelativePath(root, fullPath).Replace('\\', '/')} (different filesystem)"
            : null;

        /// <summary>The walk stays on the root's device and this folder is not on it (an unreadable device counts as another).</summary>
        private bool OnAnotherDevice(string fullPath) => device is { } own && deviceOf(fullPath) != own;

        private bool Exclude(string why)
        {
            if (!_named.Contains(why, StringComparer.Ordinal))
            {
                Add(why);
            }

            return false;
        }

        private void Add(string why)
        {
            if (_named.Count < MaxExclusionsNamed)
            {
                _named.Add(why);
            }
            else
            {
                _more++;
            }
        }
    }

    /// <summary>What a counting walk notes on the side: the entries it could not open, the newest write it saw, the named entries it met.</summary>
    private sealed class Probe(TreeRules rules)
    {
        private readonly List<string> _found = [];

        public int Denied { get; set; }

        public DateTimeOffset Newest { get; private set; } = DateTimeOffset.MinValue;

        public IReadOnlyList<string> Found => _found.Count == 0 ? Array.Empty<string>() : [.. _found];

        public void See(ref FileSystemEntry entry)
        {
            if (rules.TrackNewest && entry.LastWriteTimeUtc > Newest)
            {
                Newest = entry.LastWriteTimeUtc;
            }

            if (rules.ReportNames.Count > 0 && _found.Count < rules.ReportNamesMax && rules.ReportNames.Contains(entry.FileName.ToString()))
            {
                _found.Add(entry.ToFullPath());
            }
        }
    }

    /// <summary>The walk that COUNTS (<see cref="TreeRules.CountDenied"/>): <see cref="EnumerationOptions.IgnoreInaccessible"/> is off and
    /// <see cref="FileSystemEnumerator{TResult}.ContinueOnError"/> counts each entry the enumeration could not open and goes on — an
    /// unreadable subtree is reported, never passed over in silence. Every entry is seen (<see cref="Probe"/>) before it is judged.</summary>
    private sealed class CountingEntries(string root, EnumerationOptions options, TreeRules rules, WalkState state, Exclusions excluded, Probe probe) : IEnumerable<(long Length, bool Counted, TreeFile? File)>
    {
        public IEnumerator<(long Length, bool Counted, TreeFile? File)> GetEnumerator() => new Enumerator(root, options, rules, state, excluded, probe);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Enumerator(string directory, EnumerationOptions options, TreeRules rules, WalkState state, Exclusions excluded, Probe probe) : FileSystemEnumerator<(long Length, bool Counted, TreeFile? File)>(directory, options)
        {
            private readonly string _root = directory;

            protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
            {
                probe.See(ref entry);
                return !entry.IsDirectory;
            }

            protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry)
            {
                var name = entry.FileName.ToString();
                var full = entry.ToFullPath();
                return !state.Stopped && !rules.SkipDirectory(full, name) && excluded.MayEnter(rules, name, full);
            }

            protected override (long Length, bool Counted, TreeFile? File) TransformEntry(ref FileSystemEntry entry) =>
                (entry.Length, rules.CountOnlyUnder.Count == 0 || IsUnderNamed(_root, entry.Directory.ToString(), rules.CountOnlyUnder), rules.ListFiles ? new TreeFile(entry.ToFullPath(), entry.Length, entry.LastWriteTimeUtc) : null);

            protected override bool ContinueOnError(int error)
            {
                if (rules.CountDenied)
                {
                    probe.Denied++;
                }

                return true;
            }
        }
    }

    private sealed class WalkState(Stopwatch watch, TreeLimits limits)
    {
        public bool Stopped { get; set; }

        /// <summary>Why the walk must stop now, or empty.</summary>
        public string Exhausted(long visited) =>
            visited >= limits.MaxEntries
                ? $"stopped after {limits.MaxEntries} entries; the figures are a lower bound"
                : watch.Elapsed > limits.MaxDuration
                    ? $"stopped after {limits.MaxDuration.TotalSeconds:0} s; the figures are a lower bound"
                    : string.Empty;
    }
}
