using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Folders;
using WslCare.Core.Hosting;

namespace WslCare.Core.Agents;

/// <summary>One agent to measure: its catalogue entry, its data folders as paths on this side, and where its session layout
/// starts (empty = sessions not counted here), and — for a manual agent the rules refused — why it is not walked.</summary>
public sealed record AgentTarget(AgentEntry Entry, IReadOnlyList<string> Folders, string SessionsUnder, string Refusal = "");

/// <summary>The sessions an agent's listing found, with their figures and the five largest by name.</summary>
public sealed record SessionsMeasured(SessionFigures Figures, IReadOnlyList<SessionName> Largest)
{
    public static SessionsMeasured NotCounted(string why) => new(SessionFigures.NotCounted(why), []);
}

/// <summary>
/// Measures the AI agents' data folders (plan §4.6, §15q D1, R2.3) — sizes and counts, nothing read, moved or deleted
/// (H1–H3): a bounded walk per folder that never follows a link, never enters <see cref="AgentCatalogue.Memory"/> (of ANY
/// agent, whatever its case, H2) nor the entry's own never-enter names and prefixes, and stops at a change of filesystem
/// (review C1); and the sessions of a confirmed layout by listing names (<see cref="SessionGlob"/>), each session sized with
/// its companions (D2's one session, review R7).
/// </summary>
/// <remarks>
/// <para>ONE total budget for the whole walk (review M7): catalogue order, each folder under the per-folder ceiling of the
/// daily walk or what is left of the budget, whichever is less; what the budget does not reach is "not measured this run" —
/// a reason, never 0.</para>
/// <para><b>Where a walk may start</b> (review S3): a folder — a catalogue one or a manual one — is walked or listed only when
/// its REAL path is its spelled place under the home's real path (no link in any component below the home) and it lies on the
/// home's filesystem; otherwise it is "not walked" with why. A mount or a link to <c>/mnt/c</c> is never walked over 9p.</para>
/// </remarks>
public sealed class AgentWalk(IFileSystem files, TimeProvider clock, string home)
{
    /// <summary>The whole agent walk inside a root <c>collect</c> (review M7).</summary>
    public static TimeSpan CollectBudget => Tuning.Current.Seconds(ConfigKeys.Agents.WalkBudgetSeconds);

    /// <summary><c>agents list --measure</c> and <c>agents probe</c>: below every host call's timeout (E7.S4 states the client's).</summary>
    public static TimeSpan MeasureNowBudget => Tuning.Current.Seconds(ConfigKeys.Agents.MeasureBudgetSeconds);

    public const string NotReached = "not measured this run: the agent walk's time budget was spent before this folder";

    private const string OtherFilesystem = " (different filesystem)";

    /// <summary>Measures <paramref name="targets"/> within <paramref name="budget"/>; <paramref name="withNames"/> keeps the five
    /// largest sessions by name and the names of the folders left out (a live answer only — never what a history line carries).</summary>
    public AgentsSample Measure(IReadOnlyList<AgentTarget> targets, TimeSpan budget, bool withNames, CancellationToken cancellationToken)
    {
        var started = clock.GetTimestamp();
        var limit = new Budget(() => budget - clock.GetElapsedTime(started), cancellationToken);
        var agents = targets.Select(t => MeasureOne(t, limit, withNames)).ToList();
        return new AgentsSample(clock.GetUtcNow(), agents);
    }

    /// <summary>What a walk of this entry declines to enter.</summary>
    public static TreeRules RulesFor(AgentEntry entry) =>
        new(new HashSet<string>(StringComparer.Ordinal), NeverEnterOf(entry)) { NeverEnterPrefixes = entry.NeverEnterPrefixes, StayOnDevice = true };

    /// <summary><see cref="AgentCatalogue.Memory"/> and the entry's own names — compared whatever the case (review S8: over-exclusion
    /// is the safe side).</summary>
    public static IReadOnlySet<string> NeverEnterOf(AgentEntry entry) =>
        new HashSet<string>([AgentCatalogue.Memory, .. entry.NeverEnter], StringComparer.OrdinalIgnoreCase);

    /// <summary>Why <paramref name="path"/> may not be walked or listed (review S3); empty when it may: its real path must be its
    /// spelled place under the real home, and its device the home's.</summary>
    public static string PlaceProblem(IFileSystem files, string home, string path) =>
        files.ResolvePath(path) is not RealPathResult.Resolved real ? $"not walked: {path} cannot be resolved"
        : !SamePath(real.Path, Expected(files, home, path)) ? $"not walked: a link on the way ({path} leads to {real.Path})"
        : files.DeviceOf(path) is not { } device || files.DeviceOf(home) != device ? $"not walked: {path} is on another filesystem than the home"
        : string.Empty;

    /// <summary>Where <paramref name="path"/> would really be with no link below the home: the home's real path joined with the rest.</summary>
    private static string Expected(IFileSystem files, string home, string path)
    {
        var full = Path.GetFullPath(path);
        var fullHome = Path.GetFullPath(home);
        var realHome = files.ResolvePath(home) is RealPathResult.Resolved r ? r.Path : fullHome;
        return PathRules.ForThisOs.IsStrictlyUnder(full, fullHome) ? Path.Join(realHome, Path.GetRelativePath(fullHome, full)) : full;
    }

    private static bool SamePath(string a, string b) => PathRules.ForThisOs.PathEquals(Path.GetFullPath(a), Path.GetFullPath(b));

    private AgentSize MeasureOne(AgentTarget target, Budget limit, bool withNames)
    {
        if (target.Refusal.Length > 0)
        {
            return Refused(target);
        }

        var rules = RulesFor(target.Entry);
        var folders = target.Folders.Select(f => MeasureFolder(f, rules, limit)).Select(f => withNames ? f : Unnamed(f)).ToList();
        var sessions = CountSessions(target, rules, limit);
        return new AgentSize(target.Entry.Id, folders, sessions.Figures) { Largest = withNames ? sessions.Largest : null };
    }

    private AgentFolderSize MeasureFolder(string path, TreeRules rules, Budget limit)
    {
        if (!files.DirectoryExists(path))
        {
            return new AgentFolderSize(path, false, 0, 0, true, [], $"{path} does not exist");
        }

        if (PlaceProblem(files, home, path) is { Length: > 0 } problem)
        {
            return new AgentFolderSize(path, true, 0, 0, false, [], problem);
        }

        // The time left is read ONCE: a walk is never started with a ceiling the budget no longer holds.
        var remaining = limit.Left();
        return remaining <= TimeSpan.Zero
            ? new AgentFolderSize(path, true, 0, 0, false, [], NotReached)
            : Sized(path, files.WalkTree(path, Limits(remaining), rules, limit.Token));
    }

    private static TreeLimits Limits(TimeSpan remaining) =>
        new(FolderSizes.Limits.MaxEntries, remaining < FolderSizes.Limits.MaxDuration ? remaining : FolderSizes.Limits.MaxDuration);

    /// <summary>Review R9: what a history line keeps of the folders left out — a count, never a folder's name (a project's name
    /// is the user's).</summary>
    private static AgentFolderSize Unnamed(AgentFolderSize folder)
    {
        var other = folder.Excluded.Count(e => e.EndsWith(OtherFilesystem, StringComparison.Ordinal));
        return other == 0 ? folder : folder with { Excluded = [.. folder.Excluded.Where(e => !e.EndsWith(OtherFilesystem, StringComparison.Ordinal)), $"{other} folder(s) on another filesystem"] };
    }

    /// <summary>A manual agent the rules refused (plan §15q R2.1): nothing under it is entered, and every figure says why.</summary>
    private static AgentSize Refused(AgentTarget target)
    {
        var why = $"not walked: {target.Refusal}";
        return new AgentSize(target.Entry.Id, [.. target.Folders.Select(f => new AgentFolderSize(f, false, 0, 0, false, [], why))], SessionFigures.NotCounted(why));
    }

    private static AgentFolderSize Sized(string path, TreeMeasure measure) => measure switch
    {
        TreeMeasure.Measured m => new AgentFolderSize(path, true, m.Bytes, m.Files, m.Complete, m.Excluded, m.Note),
        TreeMeasure.Missing => new AgentFolderSize(path, false, 0, 0, true, [], $"{path} does not exist"),
        TreeMeasure.Unreadable u => new AgentFolderSize(path, true, 0, 0, false, [], u.Reason),
        _ => throw new System.Diagnostics.UnreachableException("TreeMeasure is a closed set"),
    };

    private SessionsMeasured CountSessions(AgentTarget target, TreeRules rules, Budget limit) =>
        target.Entry.Sessions is not { } layout || target.SessionsUnder.Length == 0
            ? SessionsMeasured.NotCounted("monitor only: this agent's session layout is not confirmed on this side")
            : Startable(target.SessionsUnder, limit) is { Length: > 0 } why ? SessionsMeasured.NotCounted(why)
            : Listed(target, layout, rules, limit);

    /// <summary>Why a session listing does not start: the folder is missing, it is not where it seems (S3), or no time is left.</summary>
    private string Startable(string under, Budget limit) =>
        !files.DirectoryExists(under) ? $"{under} does not exist"
        : PlaceProblem(files, home, under) is { Length: > 0 } problem ? problem
        : limit.Left() <= TimeSpan.Zero ? NotReached
        : string.Empty;

    private SessionsMeasured Listed(AgentTarget target, AgentSessionLayout layout, TreeRules rules, Budget limit)
    {
        var listing = new SessionListing(files, rules.NeverEnter, () => limit.Left() <= TimeSpan.Zero, limit.Token) { Device = files.DeviceOf(target.SessionsUnder) };
        var scan = SessionGlob.Find(listing, target.SessionsUnder, layout.Glob);
        return scan.Reached
            ? Figures(scan, new Companions(files, target.SessionsUnder, layout.Companions, rules, limit))
            : SessionsMeasured.NotCounted($"not counted: {scan.Note}");
    }

    /// <summary>Every session sized with its companions; the figures and the five largest.</summary>
    private static SessionsMeasured Figures(SessionScan scan, Companions companions)
    {
        var sized = scan.Sessions.Select(s => s with { Session = s.Session with { Bytes = s.Session.Bytes + companions.BytesOf(s.Session.Name) } }).ToList();
        var largest = sized.Select(s => s.Session).OrderByDescending(s => s.Bytes).ThenBy(s => s.Name, StringComparer.Ordinal).Take(5).ToList();
        var note = string.Join("; ", new[] { scan.Note, companions.Note }.Where(n => n.Length > 0));
        return new SessionsMeasured(
            new SessionFigures(
                true,
                sized.Count,
                sized.Count == 0 ? null : sized.Min(s => s.LastWrite),
                sized.Count == 0 ? null : sized.Max(s => s.LastWrite),
                largest.Count == 0 ? 0 : largest[0].Bytes,
                note.Length == 0,
                note),
            largest);
    }

    /// <summary>What is left of the walk's one budget, and its cancellation.</summary>
    private sealed record Budget(Func<TimeSpan> Left, CancellationToken Token);

    /// <summary>
    /// D2's one session beyond its transcript (review R7): the companion files and folders the layout names — <c>{dir}</c> the
    /// session file's folder, <c>{id}</c> its name without the extension — sized by stat and by the same walk rules (memory never
    /// entered, no link followed, the device kept), each under what is left of the budget.
    /// </summary>
    private sealed class Companions(IFileSystem files, string under, IReadOnlyList<string> patterns, TreeRules rules, Budget limit)
    {
        public string Note { get; private set; } = string.Empty;

        public long BytesOf(string sessionName)
        {
            var name = sessionName.Replace('\\', '/');
            var dir = name.Contains('/', StringComparison.Ordinal) ? name[..name.LastIndexOf('/')] : string.Empty;
            var id = Path.GetFileNameWithoutExtension(name);
            return patterns.Sum(p => SizeOf(Path.Join(under, p.Replace("{dir}", dir, StringComparison.Ordinal).Replace("{id}", id, StringComparison.Ordinal))));
        }

        private long SizeOf(string path) =>
            files.ReadLink(path) is LinkReadResult.Target ? 0
            : files.DirectoryExists(path) ? FolderSize(path)
            : files.FileSize(path) is FileSizeResult.Measured m ? m.Bytes : 0;

        private long FolderSize(string path)
        {
            var remaining = limit.Left();
            if (remaining <= TimeSpan.Zero)
            {
                Note = "the companions of some sessions were not measured (the time budget); the sizes are a lower bound";
                return 0;
            }

            return files.WalkTree(path, Limits(remaining), rules, limit.Token) is TreeMeasure.Measured m ? m.Bytes : 0;
        }
    }
}
