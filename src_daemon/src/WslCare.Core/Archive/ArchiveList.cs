using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Records;

namespace WslCare.Core.Archive;

/// <summary>What <c>archive list</c> was asked for: optionally one agent, one month (<c>yyyy-MM</c>), the entries one run touched.</summary>
public sealed record ArchiveListRequest(string Agent, string Month, string RunId)
{
    /// <summary><c>--restorable</c> (plan §15r E9.S4, the code round's finding 4): only the VERIFIED entries removed at their source,
    /// newest first, at most <c>archive.maxRestoreEntries</c> — what A20 offers, inside the child's answer cap.</summary>
    public bool Restorable { get; init; }

    /// <summary>With <see cref="Restorable"/>: only these entries (the E10.S0 own review, finding 1) — A20's preview asks for the shown ids,
    /// so an entry older than the newest <c>archive.maxRestoreEntries</c> is never lost to the window; empty = every restorable one.</summary>
    public IReadOnlyList<string> EntryIds { get; init; } = [];
}

/// <summary>One archived entry as the index of this side says it: its status, whether every line of it is this side's, its size.</summary>
public sealed record ArchiveListEntry(string EntryId, string Agent, string Key, string Month, string Status, bool Verified, int Files, long Bytes, DateTimeOffset ArchivedAtUtc);

/// <summary>The answer of <c>archive list --json</c>.</summary>
/// <param name="SkippedLines">Index lines the reader skipped (torn, malformed, hostile).</param>
/// <param name="Notes">The months whose index could not be read, by name — never read as empty.</param>
public sealed record ArchiveListReport(int SchemaVersion, string Side, string SideFolder, string BaseFolder, string Outcome, string Note, IReadOnlyList<ArchiveListEntry> Entries, int SkippedLines, IReadOnlyList<string> Notes)
{
    /// <summary>The restorable entries a <c>--restorable</c> list left out past <c>archive.maxRestoreEntries</c>; 0 otherwise.</summary>
    public int Omitted { get; init; }

    /// <summary>The effective <c>archive.maxRestoreEntries</c> — the most entries ONE restore takes (plan §15s D6, the plan round's
    /// finding 1): the extension's Archive page counts a selection against it before it offers <i>Restore</i>. On every answer.</summary>
    public int RestoreCeiling { get; init; }
}

/// <summary>One month index of one agent on this side, as it was read.</summary>
public sealed record MonthEntries(ArchiveTarget Target, string Month, MonthRead Read);

/// <summary>
/// Plan §15r E9.S3 — <c>archive list</c>, as the user, read-only: no lock, no lease, no key made (an index read without the side's key
/// reads every line unverified). Only the months asked are read, each under <c>archive.maxIndexBytes</c>; a torn, malformed or
/// hostile line is skipped and counted; an unverified entry is marked; an index that cannot be read is named, never shown empty.
/// </summary>
public static class ArchiveList
{
    public static ArchiveListReport List(ArchiveRunInput asked, ArchiveListRequest request)
    {
        var within = BaseWindow.Judged(asked, Unlistable);
        if (within.Stop.Stopped)
        {
            return Report(within.Input, within.Stop.Outcome, within.Stop.Why, [], 0, []);
        }

        var input = within.Input;
        var state = new ArchiveState(input.Paths, input.Files);
        var context = ArchiveRun.ContextOf(input, state, state.ExistingIndexKey(), InUseView.NotChecked("a list asks no scan"), static (_, _) => { });
        var targets = ArchiveTargets.Of(input.Paths, input.Files, input.Config, request.Agent, input.Environment);
        var months = Months(context, input.Files, targets, MonthOf(request.Month)).ToList();
        var entries = months.SelectMany(m => Listed(m, request.RunId)).OrderBy(e => e.Agent, StringComparer.Ordinal).ThenBy(e => e.Month, StringComparer.Ordinal).ThenBy(e => e.Key, StringComparer.Ordinal).ToList();
        var skipped = months.Sum(m => m.Read is MonthRead.Read read ? read.Index.Skipped : 0);
        var notes = months.Where(m => m.Read is MonthRead.Unreadable).Select(m => $"{m.Target.Entry.Id} {m.Month}: its index could not be read ({((MonthRead.Unreadable)m.Read).Why})").ToList();
        var kept = request.Restorable ? RestorableOf(Asked(entries, request.EntryIds), input.Config.Int(ConfigKeys.Archive.MaxRestoreEntries)) : new KeptEntries(entries, 0);
        return Report(input, RunOutcomes.Done, string.Empty, kept.Entries, skipped, [.. MountNote(input, state), .. notes]) with { Omitted = kept.Omitted };
    }

    /// <summary>The entries A20's preview asked for (the E10.S0 own review, finding 1) — every entry when it asked for none.</summary>
    private static IReadOnlyList<ArchiveListEntry> Asked(IReadOnlyList<ArchiveListEntry> entries, IReadOnlyList<string> ids) =>
        ids.Count == 0 ? entries : Only(entries, ids.ToHashSet(StringComparer.Ordinal));

    private static IReadOnlyList<ArchiveListEntry> Only(IReadOnlyList<ArchiveListEntry> entries, HashSet<string> ids) => [.. entries.Where(e => ids.Contains(e.EntryId))];

    /// <summary>What a list answers, and how many restorable entries it left out.</summary>
    private sealed record KeptEntries(IReadOnlyList<ArchiveListEntry> Entries, int Omitted);

    /// <summary>The VERIFIED entries removed at their source (<c>sourceRemoved</c>, <c>split</c>), newest first, at most
    /// <paramref name="most"/> — what A20 offers (the S4 code round, finding 4).</summary>
    private static KeptEntries RestorableOf(IReadOnlyList<ArchiveListEntry> entries, int most)
    {
        var restorable = entries.Where(e => e.Verified && e.Status is ArchiveIndex.Events.SourceRemoved or ArchiveIndex.Events.Split).OrderByDescending(e => e.ArchivedAtUtc).ToList();
        return new KeptEntries([.. restorable.Take(most)], Math.Max(0, restorable.Count - most));
    }

    /// <summary>Owner decision 2026-10-07: the list reads only, so a changed mount does not refuse it (a run would be) — the answer says
    /// so instead: what it read may be a plain folder in place of the share. Nothing is recorded.</summary>
    private static IEnumerable<string> MountNote(ArchiveRunInput input, ArchiveState state)
    {
        var now = ArchiveRun.MountOf(input.JudgedBase);
        return state.Base() is { } recorded && recorded.Folder == now.Folder && ArchiveRun.MountChange(recorded, now) is { Length: > 0 } changed
            ? [$"{changed}: what is listed may be a plain folder in place of the share; a run refuses until it is mounted as before"]
            : [];
    }

    /// <summary>Every month index of <paramref name="targets"/> on this side — only <paramref name="month"/> (<c>yyyy/MM</c>) when given.</summary>
    public static IEnumerable<MonthEntries> Months(MoveContext c, IFileSystem files, IEnumerable<ArchiveTarget> targets, string month) =>
        targets.SelectMany(t => ArchiveScan.Months(files, Path.Combine(c.BaseFolder, t.Entry.Id))
            .Where(m => month.Length == 0 || m == month)
            .Select(m => new MonthEntries(t, m, MonthIndex.Open(c, t.Entry.Id, m))));

    /// <summary><c>yyyy-MM</c> as the base's folders spell a month (<c>yyyy/MM</c>); empty stays empty.</summary>
    public static string MonthOf(string asked) => asked.Replace('-', '/');

    private static IEnumerable<ArchiveListEntry> Listed(MonthEntries month, string runId)
    {
        if (month.Read is not MonthRead.Read read)
        {
            return [];
        }

        var touched = read.Index.Records.Where(r => runId.Length == 0 || r.Line.RunId == runId).Select(r => r.Line.EntryId).ToHashSet(StringComparer.Ordinal);
        return ArchiveIndex.Merge(read.Index.Records).Where(e => touched.Contains(e.EntryId))
            .Select(e => new ArchiveListEntry(e.EntryId, e.Agent, e.Key, e.Month, e.Status, e.Verified, e.Files.Count, e.Files.Sum(f => f.Bytes), e.ArchivedAtUtc));
    }

    /// <summary>Why nothing is listed; <see cref="EarlyStop.None"/> when the base may be read — asked inside the bounded window, the
    /// base judged there too (the S4 own review round S-M1).</summary>
    private static EarlyStop Unlistable(ArchiveRunInput input) =>
        ArchiveRun.BaseProblem(input) is { Stopped: true } early ? early : BaseWindow.Reachability(input);

    private static ArchiveListReport Report(ArchiveRunInput input, string outcome, string note, IReadOnlyList<ArchiveListEntry> entries, int skipped, IReadOnlyList<string> notes) =>
        new(SchemaVersion.Current, input.Paths.Side == Hosting.HostSide.Wsl ? "wsl" : "windows", ArchiveRun.SideOf(input), input.JudgedBase.Folder, outcome, note, entries, skipped, notes)
        {
            RestoreCeiling = input.Config.Int(ConfigKeys.Archive.MaxRestoreEntries),
        };
}
