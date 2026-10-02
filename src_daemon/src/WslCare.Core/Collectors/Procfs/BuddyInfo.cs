using System.Globalization;

namespace WslCare.Core.Collectors.Procfs;

/// <summary>One line of <c>/proc/buddyinfo</c>: a memory zone and its free blocks per order (index 0 is
/// order 0, one page; order <c>n</c> is 2^n contiguous pages).</summary>
public sealed record BuddyZone(int Node, string Zone, IReadOnlyList<long> FreeBlocksByOrder);

/// <summary>
/// <c>/proc/buddyinfo</c> (plan §4.1): <c>Node 0, zone   Normal   147  40  77 …</c>.
/// </summary>
public static class BuddyInfo
{
    public static IReadOnlyList<BuddyZone> Parse(string text) => [.. ProcText.Lines(text).SelectMany(ParseLine)];

    /// <summary>A line that is not a zone line yields nothing (empty = skip, never a guessed zone).</summary>
    private static IEnumerable<BuddyZone> ParseLine(string line)
    {
        var parts = line.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        if (!IsZoneLine(parts, out var node))
        {
            return [];
        }

        var counts = parts.Skip(4).Select(p => long.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1).ToList();
        return counts.Contains(-1) ? [] : [new BuddyZone(node, parts[3], counts)];
    }

    private static bool IsZoneLine(string[] parts, out int node)
    {
        node = 0;
        return parts.Length >= 5 && parts[0] == "Node" && parts[2] == "zone" && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out node);
    }
}

/// <summary>
/// Free contiguous memory in one zone kind, summed over NUMA nodes (plan §4.1): how many free blocks
/// of order ≥ 4 (64 KiB with 4 KiB pages — what the 9p reads of 2026-10-01 asked for) and of order ≥ 7
/// (512 KiB — what the VMBus allocations of 2026-09-09/16 asked for) remain, and how many bytes they hold.
/// </summary>
public sealed record Fragmentation(string Zone, long BlocksOrder4Plus, long BlocksOrder7Plus, long BytesOrder4Plus, long BytesOrder7Plus)
{
    /// <summary>The orders the plan's thresholds are written in (§4.1).</summary>
    public const int SmallOrder = 4;

    public const int LargeOrder = 7;

    /// <summary>The zone the plan's thresholds read: <c>Normal</c>, where the large allocations failed.</summary>
    public const string ThresholdZone = "Normal";

    /// <summary>The <paramref name="zone"/> lines summed; unavailable when the kernel lists no such zone.</summary>
    public static Reading<Fragmentation> Of(IReadOnlyList<BuddyZone> zones, string zone, long pageSizeBytes)
    {
        var matching = zones.Where(z => z.Zone == zone).ToList();
        return matching.Count == 0
            ? Reading.Missing<Fragmentation>($"/proc/buddyinfo lists no zone {zone}")
            : Reading.Of(new Fragmentation(
                zone,
                Blocks(matching, SmallOrder),
                Blocks(matching, LargeOrder),
                Bytes(matching, SmallOrder, pageSizeBytes),
                Bytes(matching, LargeOrder, pageSizeBytes)));
    }

    private static long Blocks(IEnumerable<BuddyZone> zones, int fromOrder) =>
        zones.Sum(z => z.FreeBlocksByOrder.Skip(fromOrder).Sum());

    private static long Bytes(IEnumerable<BuddyZone> zones, int fromOrder, long pageSizeBytes) =>
        zones.Sum(z => z.FreeBlocksByOrder.Select((count, order) => order >= fromOrder ? count * (pageSizeBytes << order) : 0).Sum());
}
