using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Records;

namespace WslCare.Core.Archive;

/// <summary>What <c>archive list</c> was asked for: optionally one agent, one month (<c>yyyy-MM</c>), the entries one run touched.</summary>
public sealed record ArchiveListRequest(string Agent, string Month, string RunId);

/// <summary>One archived entry as the index of this side says it: its status, whether every line of it is this side's, its size.</summary>
public sealed record ArchiveListEntry(string EntryId, string Agent, string Key, string Month, string Status, bool Verified, int Files, long Bytes, DateTimeOffset ArchivedAtUtc);

/// <summary>The answer of <c>archive list --json</c>.</summary>
/// <param name="SkippedLines">Index lines the reader skipped (torn, malformed, hostile).</param>
/// <param name="Notes">The months whose index could not be read, by name — never read as empty.</param>
public sealed record ArchiveListReport(int SchemaVersion, string Side, string SideFolder, string BaseFolder, string Outcome, string Note, IReadOnlyList<ArchiveListEntry> Entries, int SkippedLines, IReadOnlyList<string> Notes);

/// <summary>One month index of one agent on this side, as it was read.</summary>
public sealed record MonthEntries(ArchiveTarget Target, string Month, MonthRead Read);

/// <summary>
/// Plan §15r E9.S3 — <c>archive list</c>, as the user, read-only: no lock, no lease, no key made (an index read without the side's key
/// reads every line unverified). Only the months asked are read, each under <c>archive.maxIndexBytes</c>; a torn, malformed or
/// hostile line is skipped and counted; an unverified entry is marked; an index that cannot be read is named, never shown empty.
/// </summary>
public static class ArchiveList
{
    public static ArchiveListReport List(ArchiveRunInput input, ArchiveListRequest request)
    {
        var problem = Unlistable(input);
        if (problem.Length > 0)
        {
            return Report(input, problem[..problem.IndexOf('|', StringComparison.Ordinal)], problem[(problem.IndexOf('|', StringComparison.Ordinal) + 1)..], [], 0, []);
        }

        var state = new ArchiveState(input.Paths, input.Files);
        var context = ArchiveRun.ContextOf(input, state, state.ExistingIndexKey(), InUseView.NotChecked("a list asks no scan"), static (_, _) => { });
        var targets = ArchiveTargets.Of(input.Paths, input.Files, input.Config, request.Agent, input.Environment);
        var months = Months(context, input.Files, targets, MonthOf(request.Month)).ToList();
        var entries = months.SelectMany(m => Listed(m, request.RunId)).OrderBy(e => e.Agent, StringComparer.Ordinal).ThenBy(e => e.Month, StringComparer.Ordinal).ThenBy(e => e.Key, StringComparer.Ordinal).ToList();
        var skipped = months.Sum(m => m.Read is MonthRead.Read read ? read.Index.Skipped : 0);
        var notes = months.Where(m => m.Read is MonthRead.Unreadable).Select(m => $"{m.Target.Entry.Id} {m.Month}: its index could not be read ({((MonthRead.Unreadable)m.Read).Why})").ToList();
        return Report(input, RunOutcomes.Done, string.Empty, entries, skipped, notes);
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

    /// <summary>Why nothing is listed — a marker word, then the sentence; empty when the base may be read.</summary>
    private static string Unlistable(ArchiveRunInput input) =>
        input.Config.Text(ConfigKeys.Archive.BaseFolder).Length == 0 ? $"{RunOutcomes.NoBase}|no {ConfigKeys.Archive.BaseFolder.Name} is set; the archive is not configured"
        : !input.JudgedBase.Accepted ? $"{RunOutcomes.Refused}|the base is refused by its rules ({input.JudgedBase.Rule}: {input.JudgedBase.Refusal})"
        : !input.Reachable(input.JudgedBase.Folder, TimeSpan.FromSeconds(input.Config.Int(ConfigKeys.Archive.ReachabilitySeconds))) ? $"{RunOutcomes.Unreachable}|the base did not answer within {ConfigKeys.Archive.ReachabilitySeconds.Name}"
        : string.Empty;

    private static ArchiveListReport Report(ArchiveRunInput input, string outcome, string note, IReadOnlyList<ArchiveListEntry> entries, int skipped, IReadOnlyList<string> notes) =>
        new(SchemaVersion.Current, input.Paths.Side == Hosting.HostSide.Wsl ? "wsl" : "windows", SideName.OfThisProcess(input.Paths.Side), input.JudgedBase.Folder, outcome, note, entries, skipped, notes);
}
