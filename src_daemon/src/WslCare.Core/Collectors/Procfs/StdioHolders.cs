using System.Globalization;

using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Collectors.Procfs;

/// <summary>What one scan of every process's fd table found — a closed set: who holds each target, or why the scan cannot rule a
/// holder out (and then every relay it was for is kept).</summary>
public abstract record HolderScan
{
    private HolderScan()
    {
    }

    /// <summary>Each target link (<c>pipe:[…]</c>) mapped to every pid that holds it — the relays themselves included.</summary>
    public sealed record Done(IReadOnlyDictionary<string, IReadOnlyList<int>> Holders) : HolderScan
    {
        public IReadOnlyList<int> Of(string target) => Holders.GetValueOrDefault(target) ?? [];
    }

    public sealed record Inconclusive(string Reason) : HolderScan;
}

/// <summary>
/// Who holds a relay's stdio (plan E14 S7b.2 item 2, the S7b.2 review's finding 2): every <c>/proc/&lt;pid&gt;/fd</c> read through
/// the seam, each link compared with the targets. Root reads every process's table. A process that vanished mid-scan is
/// skipped (normal churn — it holds nothing any more); a table that exists and cannot be read, or one link in it that cannot,
/// makes the WHOLE scan inconclusive, naming the pid — a holder there cannot be ruled out. Bounded by
/// <c>processes.fdScanMilliseconds</c>: past it the scan is inconclusive too.
/// </summary>
public static class StdioHolders
{
    public static HolderScan Scan(IFileSystem files, LinuxHostPaths linux, IReadOnlySet<string> targets, TimeProvider clock, TimeSpan budget, CancellationToken cancellationToken)
    {
        if (targets.Count == 0)
        {
            return new HolderScan.Done(new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal));
        }

        var started = clock.GetTimestamp();
        bool OutOfTime() => clock.GetElapsedTime(started) >= budget;
        var holders = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var pid in Pids(files, linux))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var why = OutOfTime()
                ? string.Create(CultureInfo.InvariantCulture, $"the scan of every process's fd table passed processes.fdScanMilliseconds ({budget.TotalMilliseconds:0} ms)")
                : ScanOne(files, linux, pid, targets, holders, new ListingBounds(int.MaxValue, OutOfTime, cancellationToken));
            if (why.Length > 0)
            {
                return new HolderScan.Inconclusive(why);
            }
        }

        return new HolderScan.Done(holders.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<int>)kv.Value, StringComparer.Ordinal));
    }

    private static IEnumerable<int> Pids(IFileSystem files, LinuxHostPaths linux) =>
        files.ListDirectories(linux.ProcRoot)
            .Select(ProcText.LastSegment)
            .Select(name => int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ? pid : 0)
            .Where(pid => pid > 0);

    /// <summary>One process's table into <paramref name="holders"/>; empty when read (or the process is gone), otherwise why the
    /// scan is inconclusive.</summary>
    private static string ScanOne(IFileSystem files, LinuxHostPaths linux, int pid, IReadOnlySet<string> targets, Dictionary<string, List<int>> holders, ListingBounds bounds)
    {
        var dir = $"{linux.ProcRoot}/{pid.ToString(CultureInfo.InvariantCulture)}";
        return files.ListEntries($"{dir}/fd", bounds) switch
        {
            EntryListing.Listed { Complete: false } cut => Gone(files, dir) ? string.Empty : $"pid {pid}'s fd table was not listed to its end ({cut.Note})",
            EntryListing.Listed listed => Links(files, dir, pid, listed.Entries, targets, holders),
            EntryListing.Unreadable unreadable => Gone(files, dir) ? string.Empty : $"pid {pid}'s fd table could not be read ({unreadable.Reason}): a holder there cannot be ruled out",
            _ => throw new System.Diagnostics.UnreachableException("EntryListing is a closed set"),
        };
    }

    private static string Links(IFileSystem files, string dir, int pid, IReadOnlyList<FileEntry> entries, IReadOnlySet<string> targets, Dictionary<string, List<int>> holders)
    {
        foreach (var entry in entries)
        {
            switch (files.ReadLink($"{dir}/fd/{entry.Name}"))
            {
                case LinkReadResult.Target { Path: var target } when targets.Contains(target):
                    Add(holders, target, pid);
                    break;
                case LinkReadResult.Unreadable unreadable when !Gone(files, dir):
                    return $"pid {pid}'s fd {entry.Name} could not be read ({unreadable.Reason}): a holder there cannot be ruled out";
                default:
                    // Another link, a descriptor closed in between, or the process gone: it holds no target.
                    break;
            }
        }

        return string.Empty;
    }

    private static void Add(Dictionary<string, List<int>> holders, string target, int pid)
    {
        if (!holders.TryGetValue(target, out var list))
        {
            holders[target] = list = [];
        }

        if (!list.Contains(pid))
        {
            list.Add(pid);
        }
    }

    private static bool Gone(IFileSystem files, string dir) => !files.DirectoryExists(dir);
}
