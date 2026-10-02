using System.Globalization;

namespace WslCare.Core.Collectors.Procfs;

/// <summary>
/// <c>/proc/meminfo</c> (plan §4.1): one <c>Key:  value [kB]</c> per line. The kernel's <c>kB</c> is
/// KiB — <c>fs/proc/meminfo.c</c> shifts pages by <c>PAGE_SHIFT - 10</c> — so a figure in bytes is the
/// value × 1024. Lines without the unit (<c>HugePages_Total</c>) are counts, not sizes, and are kept apart.
/// </summary>
public sealed record MemInfo(IReadOnlyDictionary<string, long> Kibibytes, IReadOnlyDictionary<string, long> Counts)
{
    private const long BytesPerKibibyte = 1024;

    public static MemInfo Parse(string text)
    {
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in ProcText.Lines(text))
        {
            AddLine(line, sizes, counts);
        }

        return new MemInfo(sizes, counts);
    }

    /// <summary>The figure in bytes, or why it is not there — a kernel that does not report a line is
    /// an answer, never a zero.</summary>
    public Reading<long> Bytes(string key) =>
        Kibibytes.TryGetValue(key, out var kib)
            ? Reading.Of(kib * BytesPerKibibyte)
            : Reading.Missing<long>($"/proc/meminfo has no {key} line");

    private static void AddLine(string line, Dictionary<string, long> sizes, Dictionary<string, long> counts)
    {
        var colon = line.IndexOf(':');
        if (colon > 0 && ParseValue(line[(colon + 1)..]) is { } parsed)
        {
            (parsed.IsKibibytes ? sizes : counts)[line[..colon]] = parsed.Value;
        }
    }

    /// <summary>The number after the colon and whether it carries the <c>kB</c> unit; <c>null</c> when the
    /// line holds no number (a line this parser does not understand is skipped, not guessed).</summary>
    private static (long Value, bool IsKibibytes)? ParseValue(string rest)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? (value, parts.Length > 1 && parts[1] == "kB")
            : null;
    }
}
