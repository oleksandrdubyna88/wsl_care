using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Actions.WindowsTemp;

/// <summary>One top-level entry of the temp folder, as its directory listing reports it.</summary>
/// <param name="Created">When it was created: the age is the later of its write time and its creation time.</param>
internal sealed record Candidate(string Name, string Path, EntryKind Kind, long Length, DateTimeOffset Written, DateTimeOffset Created)
{
    /// <summary>The later of the write time and the creation time — a copy keeps the write time and takes a new creation time.</summary>
    public DateTimeOffset Newest => Written >= Created ? Written : Created;
}

/// <summary>What the cleanup decided about one candidate.</summary>
internal enum TempVerdict
{
    /// <summary>Allowed, owned, clean and old: the run deletes it.</summary>
    Eligible,

    /// <summary>Old and clean but not on the allow-list: shown to the owner so the list can be widened from evidence.</summary>
    WouldBeEligible,

    /// <summary>Not on the allow-list, and not shown to be old and clean.</summary>
    NotAllowed,

    /// <summary>Allowed, but its newest descendant is newer than the age limit.</summary>
    Young,

    /// <summary>Never deleted: claude, another owner, the test-leftover sweep's, a git working folder.</summary>
    Excluded,

    /// <summary>Retained because it could not be judged.</summary>
    Unknown,

    /// <summary>An interrupted candidate that changed meanwhile, or a candidate that changed after its preview.</summary>
    Changed,

    /// <summary>The budget stopped the run before the candidate.</summary>
    Deferred,
}

/// <param name="Verdict">What was decided.</param>
/// <param name="Reason">In words.</param>
/// <param name="Group">The allow-list pattern that matched, or the derived group of a would-be-eligible name.</param>
/// <param name="Bytes">The logical bytes of its files, when it was scanned.</param>
/// <param name="NewestFile">The newest file write time inside it, when it was scanned — what a checkpoint records.</param>
internal sealed record Judgement(TempVerdict Verdict, string Reason, string Group, long Bytes, DateTimeOffset NewestFile)
{
    public static Judgement Retained(TempVerdict verdict, string reason) => new(verdict, reason, string.Empty, 0, DateTimeOffset.MinValue);

    public bool IsEligible => Verdict == TempVerdict.Eligible;
}

/// <summary>
/// The rules that decide one candidate (PLAN_windows_runner.md §3.12), in the order they are asked: the <c>claude</c> guard, the
/// candidate's OWN attributes (a reparse point is retained before any child is listed), the test-leftover prefix, the owner, the
/// allow-list, the scan of the tree (newest descendant, reparse points inside, a <c>.git</c> at the top), the age. An interrupted
/// candidate is judged by the checkpoint instead of the age (<see cref="TempCheckpoint"/>).
/// </summary>
internal sealed class TempJudge(IFileSystem files, PathRules rules, string root, string userSid, TempSettings settings, DateTimeOffset now, bool scanDisallowed)
{
    public const string GitReason = "a git working folder - never deleted";

    public const string SweepReason = "the test-leftover sweep's, not this cleanup's";

    public const string ClaudeReason = "the folder Claude Code keeps under the temp directory - never cleaned";

    public const string OwnerReason = "not owned by this account";

    public Judgement Judge(Candidate candidate, TempBudget budget, CancellationToken token, TempCheckpoint resume)
    {
        budget.Spend(1);
        return TryNever(candidate, out var never) ? never : Measure(candidate, budget, token, resume);
    }

    private bool TryNever(Candidate candidate, out Judgement judgement)
    {
        judgement = true switch
        {
            _ when TempGuards.IsClaude(rules, root, candidate.Name) => Judgement.Retained(TempVerdict.Excluded, ClaudeReason),
            _ when candidate.Kind == EntryKind.Link => Judgement.Retained(TempVerdict.Unknown, "a reparse point: never entered or followed"),
            _ when candidate.Name.StartsWith(TempGlob.TestLeftoverPrefix, StringComparison.OrdinalIgnoreCase) => Judgement.Retained(TempVerdict.Excluded, SweepReason),
            _ when !OwnedByThisAccount(candidate.Path) => Judgement.Retained(TempVerdict.Excluded, OwnerReason),
            _ => Judgement.Retained(TempVerdict.Deferred, string.Empty),
        };
        return judgement.Verdict != TempVerdict.Deferred;
    }

    private bool OwnedByThisAccount(string path) =>
        userSid.Length > 0 && string.Equals(files.OwnerSidOf(path), userSid, StringComparison.OrdinalIgnoreCase);

    private Judgement Measure(Candidate candidate, TempBudget budget, CancellationToken token, TempCheckpoint resume)
    {
        var allowed = settings.AllowedBy(candidate.Name);
        if (allowed.Length == 0 && !scanDisallowed)
        {
            return Judgement.Retained(TempVerdict.NotAllowed, "not on the allow-list");
        }

        return Scan(candidate, budget, token) switch
        {
            TreeScan.Measured measured => Decide(candidate, allowed, measured, resume),
            TreeScan.Retained retained => Judgement.Retained(TempVerdict.Unknown, retained.Reason),
            _ => throw new System.Diagnostics.UnreachableException("TreeScan is a closed set"),
        };
    }

    private TreeScan Scan(Candidate candidate, TempBudget budget, CancellationToken token) =>
        candidate.Kind == EntryKind.File
            ? new TreeScan.Measured(candidate.Newest, candidate.Written, candidate.Length, false, DateTimeOffset.MinValue)
            : TempScanner.Scan(files, rules, candidate.Path, candidate.Newest, budget, token);

    private Judgement Decide(Candidate candidate, string allowed, TreeScan.Measured scan, TempCheckpoint resume)
    {
        if (scan.HoldsGit)
        {
            return Judgement.Retained(TempVerdict.Excluded, GitReason);
        }

        return resume.IsNone ? Aged(candidate, allowed, scan) : Resumed(allowed, scan, resume);
    }

    private Judgement Aged(Candidate candidate, string allowed, TreeScan.Measured scan)
    {
        var old = scan.Newest <= now - settings.OlderThan;
        var group = allowed.Length > 0 ? allowed : TempGroups.Of(candidate.Name);
        return (allowed.Length > 0, old) switch
        {
            (true, true) => new Judgement(TempVerdict.Eligible, "older than the age limit", group, scan.Bytes, scan.NewestFile),
            (true, false) => Judgement.Retained(TempVerdict.Young, "something inside it is newer than the age limit"),
            (false, true) => new Judgement(TempVerdict.WouldBeEligible, "old and clean, but not on the allow-list", group, scan.Bytes, scan.NewestFile),
            _ => Judgement.Retained(TempVerdict.NotAllowed, "not on the allow-list"),
        };
    }

    /// <summary>An interrupted candidate: deleting children rewrites a folder's write time but never its creation time, so only its
    /// remaining FILES' write times and its folders' CREATION times say whether anything was added while it lay half deleted.</summary>
    private static Judgement Resumed(string allowed, TreeScan.Measured scan, TempCheckpoint resume) => true switch
    {
        _ when allowed.Length == 0 => Judgement.Retained(TempVerdict.Changed, "changed while interrupted: it is no longer on the allow-list"),
        _ when scan.NewestFile > resume.NewestFileWrite => Judgement.Retained(TempVerdict.Changed, "changed while interrupted: a file is newer than the checkpoint"),
        _ when scan.NewestCreated > resume.CheckpointAt => Judgement.Retained(TempVerdict.Changed, "changed while interrupted: a file or folder was created after the checkpoint"),
        _ => new Judgement(TempVerdict.Eligible, "resumes an interrupted deletion", allowed, scan.Bytes, scan.NewestFile),
    };
}

/// <summary>The group a name belongs to when no allow-list pattern names it: its leading letters-only words, then <c>-*</c>.</summary>
internal static class TempGroups
{
    public static string Of(string name)
    {
        var words = name.Split('-');
        var lead = words.Take(words.Length - 1).TakeWhile(w => w.Length > 0 && w.All(char.IsAsciiLetter)).ToList();
        return lead.Count > 0 ? string.Join('-', lead) + "-*" : new string([.. name.TakeWhile(char.IsAsciiLetter)]) + "*";
    }
}
