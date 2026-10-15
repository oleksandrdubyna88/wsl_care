using System.Globalization;

using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;

namespace WslCare.Core.Actions.WindowsTemp;

/// <summary>The keys of <see cref="ActionPreview.Carry"/> — what the preview hands the run of the same engine call.</summary>
internal static class TempCarry
{
    public const string Root = "root";
    public const string Last = "last";
    public const string PassStarted = "passStarted";
    public const string PassDecided = "passDecided";
    public const string PassesCompleted = "passesCompleted";
    public const string ResolvesInProgress = "resolvesInProgress";

    public static IReadOnlyDictionary<string, string> Of(TempPlan plan) => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Root] = plan.Root,
        [Last] = plan.Next.Last,
        [PassStarted] = plan.Next.PassStarted.ToString("O", CultureInfo.InvariantCulture),
        [PassDecided] = plan.Next.PassDecided.ToString(CultureInfo.InvariantCulture),
        [PassesCompleted] = plan.Next.PassesCompleted.ToString(CultureInfo.InvariantCulture),
        [ResolvesInProgress] = plan.ResolvesInProgress ? "true" : "false",
    };
}

/// <summary>What one run deleted and kept, by outcome and by group.</summary>
internal sealed class TempTotals
{
    private readonly Dictionary<string, (int Count, long Bytes)> _groups = new(StringComparer.Ordinal);

    public int Deleted { get; private set; }

    public long Bytes { get; private set; }

    public int KeptInUse { get; set; }

    public int Changed { get; set; }

    public int Refused { get; set; }

    public int Gone { get; set; }

    public int Unreadable { get; set; }

    public IReadOnlyDictionary<string, (int Count, long Bytes)> Groups => _groups;

    public void Deleted1(string group, long bytes)
    {
        Deleted++;
        Bytes += bytes;
        _groups[group] = _groups.TryGetValue(group, out var g) ? (g.Count + 1, g.Bytes + bytes) : (1, bytes);
    }

    public void AddBytes(long bytes) => Bytes += bytes;
}

/// <summary>
/// The run of A23 (PLAN_windows_runner.md §3.12) over what the preview selected: each candidate is RE-VALIDATED just before its
/// delete (owner, age, no reparse point, still allowed, judged afresh), a directory is checkpointed BEFORE its first delete, then
/// walked without following links; the cursor moves past every candidate it handled.
/// </summary>
internal sealed class TempRun(ActionContext context, TempSettings settings, ActionPreview preview, CancellationToken token)
{
    private readonly string _root = preview.Carry.GetValueOrDefault(TempCarry.Root, string.Empty);
    private readonly TempTotals _totals = new();
    private readonly List<ActionItem> _removed = [];
    private readonly List<ActionItem> _notRemoved = [];
    private bool _interrupted;

    /// <summary>ONE budget for every re-validation scan of the run (the Fable own review m6): the delete phase's scans are bounded by the same
    /// <c>temp.scanMaxNodes</c> / <c>temp.scanMaxSeconds</c> as the preview's, not a fresh allowance per candidate. A candidate that
    /// cannot be re-validated inside what is left is retained. (The delete walk itself has no time cap of its own: the supervisor's
    /// deadline and the cancellation end it, and the checkpoint makes that safe.)</summary>
    private readonly TempBudget _budget = new(settings.ScanMaxNodes, settings.ScanMaxTime, context.Clock);

    public ActionRun Execute()
    {
        var scope = new DeletionScope(_root, "A23");
        foreach (var target in preview.Targets)
        {
            if (token.IsCancellationRequested)
            {
                _interrupted = true;
                break;
            }

            Handle(target, scope);
        }

        return Finish();
    }

    private void Handle(ActionItem target, DeletionScope scope)
    {
        var now = context.Clock.GetUtcNow();
        var cursor = TempCursors.Read(context.Paths, context.Files);
        var resume = string.Equals(cursor.InProgress.Name, target.Key, StringComparison.OrdinalIgnoreCase) ? cursor.InProgress : TempCheckpoint.None;
        var stat = TryStat(target, out var candidate);
        if (stat != StatKind.Found)
        {
            _totals.Gone += stat == StatKind.Gone ? 1 : 0;
            _totals.Unreadable += stat == StatKind.Unreadable ? 1 : 0;
            _notRemoved.Add(new ActionItem(target.Kind, target.Name, null, stat == StatKind.Gone ? "already gone" : "unreadable: its state could not be read, so it was not deleted"));
            return;
        }

        var again = new TempJudge(context.Files, context.Paths.Rules, _root, context.UserSid, settings, now, scanDisallowed: false).Judge(candidate, _budget, token, resume);
        if (!again.IsEligible)
        {
            _totals.Changed++;
            _notRemoved.Add(new ActionItem(target.Kind, target.Name, null, $"changed since the preview: {again.Reason}"));
            Moved(cursor, candidate.Name, moveLast: resume.IsNone);
            return;
        }

        Delete(candidate, again, cursor, scope, now, resume.IsNone);
    }

    /// <summary>The candidate as it is NOW, from a stat of its own path — never a listing of the whole folder.</summary>
    private enum StatKind
    {
        Found,
        Gone,
        Unreadable,
    }

    private StatKind TryStat(ActionItem target, out Candidate candidate)
    {
        var path = target.Name;
        var files = context.Files;
        var none = new Candidate(string.Empty, path, EntryKind.File, 0, DateTimeOffset.MinValue, DateTimeOffset.MinValue);
        (candidate, var kind) = true switch
        {
            _ when files.ReadLink(path) is not LinkReadResult.NotALink => (new Candidate(target.Key, path, EntryKind.Link, 0, DateTimeOffset.MinValue, DateTimeOffset.MinValue), StatKind.Found),
            _ when files.DirectoryExists(path) => Folder(files.DirectoryLastWrite(path), target.Key, path, none),
            _ => File(files.FileSize(path), target.Key, path, none),
        };
        return kind;
    }

    private static (Candidate, StatKind) Folder(DirectoryTimeResult stat, string name, string path, Candidate none) => stat switch
    {
        DirectoryTimeResult.Measured dir => (new Candidate(name, path, EntryKind.Directory, 0, dir.ModifiedAt, dir.CreatedAt), StatKind.Found),
        DirectoryTimeResult.Unreadable => (none, StatKind.Unreadable),
        _ => (none, StatKind.Gone),
    };

    private static (Candidate, StatKind) File(FileSizeResult stat, string name, string path, Candidate none) => stat switch
    {
        FileSizeResult.Measured file => (new Candidate(name, path, EntryKind.File, file.Bytes, file.ModifiedAt, file.CreatedAt), StatKind.Found),
        FileSizeResult.Unreadable => (none, StatKind.Unreadable),
        _ => (none, StatKind.Gone),
    };

    private void Delete(Candidate candidate, Judgement judgement, TempCursor cursor, DeletionScope scope, DateTimeOffset now, bool moveLast)
    {
        var isFolder = candidate.Kind == EntryKind.Directory;
        if (isFolder && Checkpoint(cursor, candidate, judgement, now) is { Length: > 0 } failure)
        {
            _totals.Refused++;
            _notRemoved.Add(new ActionItem("folder", candidate.Path, null, $"not deleted: the checkpoint could not be written ({failure})"));
            return;
        }

        var done = isFolder
            ? TempDeletion.DeleteTree(context.Files, context.Paths.Rules, scope, candidate.Path, token)
            : TempDeletion.DeleteFile(context.Files, context.Paths.Rules, scope, candidate.Path, candidate.Length);
        Account(candidate, judgement, done);
        if (done.Cancelled)
        {
            _interrupted = true;
            return;
        }

        Moved(TempCursors.Read(context.Paths, context.Files), candidate.Name, moveLast);
    }

    /// <summary>Written, atomically, BEFORE the first delete inside the candidate. Empty when written.</summary>
    private string Checkpoint(TempCursor cursor, Candidate candidate, Judgement judgement, DateTimeOffset now) =>
        TempCursors.Write(context.Paths, context.Files, cursor with { InProgress = new TempCheckpoint(candidate.Name, judgement.NewestFile, now, context.UserSid) });

    private void Account(Candidate candidate, Judgement judgement, TreeDeletion done)
    {
        var kind = candidate.Kind == EntryKind.Directory ? "folder" : "file";
        if (done.Emptied)
        {
            _totals.Deleted1(judgement.Group, done.Bytes);
            _removed.Add(new ActionItem(kind, candidate.Path, done.Bytes, judgement.Group));
            return;
        }

        _totals.AddBytes(done.Bytes);
        _totals.KeptInUse += done.Kept.Count;
        _totals.Changed += done.Stop.StartsWith("a link", StringComparison.Ordinal) ? 1 : 0;
        _totals.Refused += done.Stop.StartsWith("the deletion policy", StringComparison.Ordinal) ? 1 : 0;
        _notRemoved.Add(new ActionItem(kind, candidate.Path, done.Bytes, Retained(done)));
    }

    private static string Retained(TreeDeletion done) =>
        done.Stop.Length > 0 ? $"changed: {done.Stop}"
        : done.Cancelled ? "cut off by a cancellation; the next run takes it first"
        : $"kept in use: {done.Kept.Count} left ({string.Join("; ", done.Kept.Take(3))})";

    /// <summary>The interrupted-candidate marker cleared and, unless <paramref name="moveLast"/> is false, the cursor past <paramref name="name"/>.
    /// A RESUMED candidate belongs to an earlier pass: finishing it must not drag the cursor back to its (earlier) name (the Fable own
    /// review m4), so its cursor keeps the position the run began at.</summary>
    private void Moved(TempCursor cursor, string name, bool moveLast) =>
        TempCursors.Write(context.Paths, context.Files, cursor with { Last = moveLast ? name : cursor.Last, InProgress = TempCheckpoint.None });

    private ActionRun Finish()
    {
        if (!_interrupted)
        {
            WriteFinalCursor();
        }

        var counts = Counts();
        var shown = ActionPreview.MaxItems;
        return new ActionRun(_totals.Deleted, _totals.Bytes, "logical bytes of the files deleted, summed as they were deleted (not disk space freed)", null, null, [.. _removed.Take(shown)], [], string.Empty)
        {
            NotRemoved = [.. _notRemoved.Take(shown)],
            Notes = [counts, ByGroup(), Retained(), .. _interrupted ? ["interrupted: the cursor and the half-deleted candidate's checkpoint stay for the next run"] : Array.Empty<string>()],
            Interrupted = _interrupted,
        };
    }

    private string Counts() => string.Create(CultureInfo.InvariantCulture,
        $"deleted {_totals.Deleted}, kept in use {_totals.KeptInUse}, changed {_totals.Changed}, refused {_totals.Refused}, unreadable {_totals.Unreadable}, already gone {_totals.Gone}; logical bytes {_totals.Bytes}");

    private string ByGroup() => _totals.Groups.Count == 0
        ? "by group: none deleted"
        : "by group: " + string.Join(", ", _totals.Groups.OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Key} {g.Value.Count} ({g.Value.Bytes} bytes)")));

    private string Retained() => string.Create(CultureInfo.InvariantCulture,
        $"retained at the preview: young {Fact(TempFacts.Young)}, unknown {Fact(TempFacts.Unknown)}, excluded {Fact(TempFacts.Excluded)}, changed {Fact(TempFacts.Changed)}, not allowed {Fact(TempFacts.NotAllowed)}, would be eligible if allowed {Fact(TempFacts.WouldBeEligible)}, deferred {Fact(TempFacts.Deferred)}");

    private long Fact(string name) => preview.Facts.GetValueOrDefault(name);

    /// <summary>The run reached its end: the cursor takes the position the plan reached, the pass counters, and — when the plan decided
    /// the interrupted candidate — loses its marker.</summary>
    private void WriteFinalCursor()
    {
        var onDisk = TempCursors.Read(context.Paths, context.Files);
        var carry = preview.Carry;
        var resolves = carry.GetValueOrDefault(TempCarry.ResolvesInProgress) == "true";
        var next = onDisk with
        {
            Last = carry.GetValueOrDefault(TempCarry.Last, onDisk.Last),
            PassStarted = DateTimeOffset.TryParse(carry.GetValueOrDefault(TempCarry.PassStarted), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var started) ? started : onDisk.PassStarted,
            PassDecided = int.TryParse(carry.GetValueOrDefault(TempCarry.PassDecided), CultureInfo.InvariantCulture, out var decided) ? decided : onDisk.PassDecided,
            PassesCompleted = int.TryParse(carry.GetValueOrDefault(TempCarry.PassesCompleted), CultureInfo.InvariantCulture, out var passes) ? passes : onDisk.PassesCompleted,
            InProgress = resolves ? TempCheckpoint.None : onDisk.InProgress,
        };
        if (TempCursors.Write(context.Paths, context.Files, next) is { Length: > 0 } failure)
        {
            _notRemoved.Add(new ActionItem("state", TempCursors.File(context.Paths), null, $"the cursor could not be written: {failure}"));
        }
    }
}
