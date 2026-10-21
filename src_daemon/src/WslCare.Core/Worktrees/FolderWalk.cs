using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Worktrees;

/// <summary>
/// The walk of ONE worktree or orphan folder (E19.S3): its size, its idleness and its build-output folders — see
/// <see cref="WorktreeScanner"/> for the rules. One walk of the folder OUTSIDE its build output (counting every entry it could not open),
/// then one walk inside each build-output folder it did not enter, each under what is left of the budget.
/// </summary>
internal sealed class FolderWalk(IHostPaths paths, IFileSystem files, WorktreeSettings settings, ScanBudget budget, DateTimeOffset now, CancellationToken cancellationToken)
{
    private static readonly IReadOnlySet<string> Git = new HashSet<string>([".git"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The admin folder's files git touches on every status, add and checkout.</summary>
    private static readonly string[] AdminFiles = ["HEAD", "index", @"logs\HEAD"];

    private PathRules Rules => paths.Rules;

    public FolderScan Walk(FolderFacts facts)
    {
        var finder = new CandidateFinder(files, Rules, settings, budget, cancellationToken);
        var outside = files.WalkTree(facts.Path, budget.Limits(), OutsideRules(finder), cancellationToken);
        if (outside is not TreeMeasure.Measured measured)
        {
            return new FolderScan(facts, Unknown(outside is TreeMeasure.Unreadable u ? u.Reason : $"{facts.Path} is gone"), 0, []);
        }

        budget.Spend(measured);
        var candidates = finder.Found.Select(found => WalkCandidate(facts.Path, found)).ToList();
        return new FolderScan(facts, Idleness(facts, measured, finder.Unreadable), measured.Bytes + candidates.Sum(c => c.Bytes), candidates);
    }

    private static TreeRules OutsideRules(CandidateFinder finder) =>
        TreeRules.All with { CountDenied = true, TrackNewest = true, NeverEnter = Git, SkipDirectory = finder.Skip };

    private BuildCandidate WalkCandidate(string folder, string path)
    {
        var name = Rules.Segments(path)[^1].ToLowerInvariant();
        var relative = Rules.Normalize(path)[(Rules.Normalize(folder).Length + 1)..];
        var rules = TreeRules.All with { CountDenied = true, TrackNewest = true, ListFiles = !BuildOutputRule.IsNodeModules(name), NeverEnter = Git, ReportNames = Git };
        return files.WalkTree(path, budget.Limits(), rules, cancellationToken) switch
        {
            TreeMeasure.Measured walked => Candidate(path, relative, name, walked),
            TreeMeasure.Unreadable bad => new BuildCandidate(path, relative, name, 0, 0, $"it could not be inspected: {bad.Reason}"),
            _ => new BuildCandidate(path, relative, name, 0, 0, "it is gone"),
        };
    }

    private BuildCandidate Candidate(string path, string relative, string name, TreeMeasure.Measured walked)
    {
        budget.Spend(walked);
        return new BuildCandidate(path, relative, name, walked.Bytes, walked.Files, Retained(path, name, walked));
    }

    /// <summary>Why a candidate is kept whole; empty when it passes every structural check. A nested repository first.</summary>
    private string Retained(string path, string name, TreeMeasure.Measured walked) =>
        walked.Found.Count > 0 ? "holds a nested repository (.git)"
        : !walked.Complete ? $"its own walk did not finish ({walked.Note})"
        : walked.Denied > 0 ? $"{walked.Denied} entries in it could not be read"
        : BuildOutputRule.Retention(name, path, walked.Listed, settings);

    private IdleState Idleness(FolderFacts facts, TreeMeasure.Measured outside, int unreadableListings)
    {
        var unreadable = outside.Denied + unreadableListings;
        if (!outside.Complete)
        {
            return Unknown(outside.Note);
        }

        if (unreadable > 0)
        {
            return Unknown($"{unreadable} entries could not be read (access denied or the like); an unreadable subtree may hold a fresh write");
        }

        var times = new List<DateTimeOffset> { outside.NewestWriteUtc };
        var problems = new List<string>();
        Collect(files.DirectoryLastWrite(facts.Path), times, problems);
        foreach (var file in facts.Kind == FolderKind.Worktree ? AdminFiles : [])
        {
            Collect(files.FileSize(Rules.Join(facts.AdminFolder, file)), times, problems);
        }

        return problems.Count > 0 ? Unknown(problems[0]) : Decide(times.Max());
    }

    private static void Collect(DirectoryTimeResult result, List<DateTimeOffset> times, List<string> problems)
    {
        switch (result)
        {
            case DirectoryTimeResult.Measured measured:
                times.Add(measured.ModifiedAt);
                break;
            case DirectoryTimeResult.Unreadable unreadable:
                problems.Add($"a write time could not be read: {unreadable.Reason}");
                break;
        }
    }

    private static void Collect(FileSizeResult result, List<DateTimeOffset> times, List<string> problems)
    {
        switch (result)
        {
            case FileSizeResult.Measured measured:
                times.Add(measured.ModifiedAt);
                break;
            case FileSizeResult.Unreadable unreadable:
                problems.Add($"a write time could not be read: {unreadable.Reason}");
                break;
        }
    }

    /// <summary>Idle when the newest write is at least <c>worktrees.idleDays</c> old; a write dated later than now is never idle.</summary>
    private IdleState Decide(DateTimeOffset newest) =>
        newest > now || now - newest < TimeSpan.FromDays(settings.IdleDays) ? new IdleState(IdleKind.Active, string.Empty, newest) : new IdleState(IdleKind.Idle, string.Empty, newest);

    private static IdleState Unknown(string reason) => new(IdleKind.Unknown, reason, DateTimeOffset.MinValue);

    /// <summary>Finds the build-output folders a walk must not enter (a <c>bin</c>/<c>obj</c> beside a listed project file, a top-most
    /// <c>node_modules</c> beside a <c>package.json</c>) — the walk asks, this answers and remembers; the parent is listed once.</summary>
    private sealed class CandidateFinder(IFileSystem files, PathRules rules, WorktreeSettings settings, ScanBudget budget, CancellationToken cancellationToken)
    {
        private readonly Dictionary<string, (bool Project, bool Package)> _beside = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Found { get; } = [];

        /// <summary>Listings of a parent folder that could not be read: the folder cannot be judged idle.</summary>
        public int Unreadable { get; private set; }

        public bool Skip(string fullPath, string name)
        {
            var node = BuildOutputRule.IsNodeModules(name);
            if (!node && !BuildOutputRule.IsProjectFolderName(name))
            {
                return false;
            }

            var beside = Beside(rules.Parent(fullPath));
            if (!(node ? beside.Package : beside.Project))
            {
                return false;
            }

            Found.Add(fullPath);
            return true;
        }

        private (bool Project, bool Package) Beside(string parent)
        {
            if (_beside.TryGetValue(parent, out var known))
            {
                return known;
            }

            var listed = files.ListEntries(parent, new ListingBounds(settings.MaxEntries, () => budget.OutOfTime, cancellationToken));
            if (listed is not EntryListing.Listed entries)
            {
                Unreadable++;
                return _beside[parent] = (false, false);
            }

            var siblings = entries.Entries.Where(e => e.Kind == EntryKind.File).ToList();
            return _beside[parent] = (
                siblings.Any(e => settings.ProjectFileExtensions.Contains(Path.GetExtension(e.Name), StringComparer.OrdinalIgnoreCase)),
                siblings.Any(e => e.Name.Equals("package.json", StringComparison.OrdinalIgnoreCase)));
        }
    }
}
