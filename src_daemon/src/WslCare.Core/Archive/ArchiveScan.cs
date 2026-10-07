using System.Text.RegularExpressions;

using WslCare.Core.Files;
using WslCare.Core.Folders;

namespace WslCare.Core.Archive;

/// <summary>What <c>archive reconcile --scan</c> found: the side folders it walked, the archived files it saw, the ones it re-indexed.</summary>
public sealed record ScanReport(int SideFolders, int Files, int Recovered, IReadOnlyList<string> Notes);

/// <summary>
/// Plan §15r D3 — <c>archive reconcile --scan</c>: explicit and bounded (the walk's own limits). For every agent this side archives,
/// every <c>&lt;agent&gt;/&lt;yyyy&gt;/&lt;MM&gt;/&lt;side&gt;/</c> folder of the base is walked; an archived file no index line of that month names
/// — a copy a crash left before its line was written — is hashed and re-indexed as <c>recovered</c>, one entry per file. Restoring
/// a recovered entry needs <c>--accept-unverified</c> (E9.S3); NOTHING at the source is touched.
/// </summary>
public static partial class ArchiveScan
{
    /// <summary>The write probe a SIGKILL may leave inside the base (§15r review round S-probe) — never an archived file.</summary>
    private const string ProbeMark = ".wsl-care-write-probe-";

    public static ScanReport Scan(MoveContext c, IFileSystem files, IEnumerable<string> agents, CancellationToken token)
    {
        var report = new ScanReport(0, 0, 0, []);
        foreach (var agent in agents.Distinct(StringComparer.Ordinal))
        {
            foreach (var month in Months(files, Path.Combine(c.BaseFolder, agent)))
            {
                report = Month(c, files, agent, month, report, token);
            }
        }

        return report;
    }

    /// <summary>Every <c>yyyy/MM</c> folder under an agent's folder of the base.</summary>
    private static IEnumerable<string> Months(IFileSystem files, string agentFolder) =>
        !files.DirectoryExists(agentFolder) ? []
        : files.ListDirectories(agentFolder).Select(Path.GetFileName).Where(y => Year().IsMatch(y ?? string.Empty))
            .SelectMany(y => files.ListDirectories(Path.Combine(agentFolder, y!)).Select(Path.GetFileName).Where(m => MonthName().IsMatch(m ?? string.Empty)).Select(m => $"{y}/{m}"));

    private static ScanReport Month(MoveContext c, IFileSystem files, string agent, string month, ScanReport report, CancellationToken token)
    {
        var sideFolder = Path.Combine([c.BaseFolder, .. ArchiveCopy.Levels(agent, month, c.Side, [])]);
        if (!files.DirectoryExists(sideFolder))
        {
            return report;
        }

        var index = MonthIndex.Open(c, agent, month);
        if (index is MonthRead.Unreadable unreadable)
        {
            // Correctness review M6: an index that could not be read is not an empty one — re-indexing the month would add a line
            // for every file it already names.
            return report with { Notes = [.. report.Notes, $"{agent} {month}: its index could not be read ({unreadable.Why}); nothing of the month was re-indexed"] };
        }

        var known = (index is MonthRead.Read read ? ArchiveIndex.Merge(read.Index.Records) : []).SelectMany(e => e.Files.Select(f => f.Archived)).ToHashSet(StringComparer.Ordinal);
        var listed = files.WalkTree(sideFolder, FolderSizes.Limits, new TreeRules(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal)) { ListFiles = true }, token) switch
        {
            TreeMeasure.Measured measured => measured.Listed,
            _ => [],
        };
        var archived = listed.Select(f => (File: f, Relative: Path.GetRelativePath(sideFolder, f.Path).Replace('\\', '/')))
            .Where(f => f.Relative != ArchiveIndex.FileName && !Path.GetFileName(f.Relative).StartsWith(ProbeMark, StringComparison.Ordinal) && ArchiveIndex.IsPlainRelative(f.Relative))
            .ToList();
        var recovered = archived.Where(f => !known.Contains(f.Relative)).Count(f => Recovered(c, agent, month, f.Relative, f.File.LastWriteUtc));
        return report with { SideFolders = report.SideFolders + 1, Files = report.Files + archived.Count, Recovered = report.Recovered + recovered };
    }

    /// <summary>One unindexed archived file hashed through the seam and indexed as <c>recovered</c>.</summary>
    private static bool Recovered(MoveContext c, string agent, string month, string relative, DateTimeOffset lastWrite)
    {
        var levels = ArchiveCopy.Levels(agent, month, c.Side, ArchiveCopy.Folders(relative));
        if (c.Files.OpenExistingFolderBeneath(c.BaseFolder, levels) is not FolderBeneath.Ready { Folder: var folder })
        {
            return false;
        }

        FileHash hashed;
        using (folder)
        {
            hashed = c.Files.ReadBack(folder, Path.GetFileName(relative));
        }

        if (hashed is not FileHash.Hashed { Sha256: var sha, Length: var length })
        {
            return false;
        }

        var key = relative;
        var line = new IndexLine(ArchiveIndex.SchemaVersion, ArchiveIndex.Events.Recovered, ArchiveIndex.EntryIdOf(c.Side, agent, "recovered:" + key, [sha]), agent, c.Side, key, month, c.Clock.GetUtcNow(), c.Zone.Id, c.RunId, [new IndexFile(relative, relative, length, sha, lastWrite)], string.Empty);
        return ArchiveCopy.AppendLine(c, agent, month, line).Length == 0;
    }

    [GeneratedRegex("^[0-9]{4}$")]
    private static partial Regex Year();

    [GeneratedRegex("^[0-9]{2}$")]
    private static partial Regex MonthName();
}
