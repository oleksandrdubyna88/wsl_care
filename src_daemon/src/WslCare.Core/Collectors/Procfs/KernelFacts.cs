using System.Buffers.Binary;
using System.Globalization;

namespace WslCare.Core.Collectors.Procfs;

/// <summary>
/// The two kernel constants the process figures are scaled by, read from the auxiliary vector the
/// kernel hands every process (<c>/proc/self/auxv</c>) — the same source glibc's <c>sysconf</c> reads,
/// so no native call is needed and a captured fixture carries its own values.
/// </summary>
/// <param name="ClockTicksPerSecond"><c>AT_CLKTCK</c>: the unit of <c>utime</c>, <c>stime</c> and
/// <c>starttime</c> in <c>/proc/[pid]/stat</c>. Probed 2026-10-02 on this machine's WSL kernel
/// (6.18.33.2, x86_64): 100, equal to <c>getconf CLK_TCK</c>.</param>
/// <param name="PageSizeBytes"><c>AT_PAGESZ</c>: the unit of <c>/proc/buddyinfo</c>'s orders. Probed the
/// same day: 4096, equal to <c>getconf PAGESIZE</c>.</param>
public sealed record KernelFacts(long ClockTicksPerSecond, long PageSizeBytes)
{
    private const ulong AtNull = 0;
    private const ulong AtPageSize = 6;
    private const ulong AtClockTick = 17;

    /// <summary>One auxv entry on the 64-bit kernels this binary ships for (x86_64, arm64): two
    /// little-endian 64-bit words, type then value.</summary>
    private const int EntryBytes = 16;

    /// <summary>The two values, or why they are not there. An unknown value is never replaced by a
    /// customary one: a figure scaled by a guessed constant is a wrong figure that looks right.</summary>
    public static Reading<KernelFacts> FromAuxVector(ReadOnlySpan<byte> auxv)
    {
        var values = Entries(auxv);
        var ticks = values.GetValueOrDefault(AtClockTick);
        var page = values.GetValueOrDefault(AtPageSize);
        return ticks > 0 && page > 0
            ? Reading.Of(new KernelFacts((long)ticks, (long)page))
            : Reading.Missing<KernelFacts>("/proc/self/auxv carries no AT_CLKTCK or AT_PAGESZ entry");
    }

    private static Dictionary<ulong, ulong> Entries(ReadOnlySpan<byte> auxv)
    {
        var entries = new Dictionary<ulong, ulong>();
        for (var offset = 0; offset + EntryBytes <= auxv.Length; offset += EntryBytes)
        {
            var type = BinaryPrimitives.ReadUInt64LittleEndian(auxv[offset..]);
            if (type == AtNull)
            {
                break;
            }

            entries[type] = BinaryPrimitives.ReadUInt64LittleEndian(auxv[(offset + 8)..]);
        }

        return entries;
    }
}

/// <summary><c>/proc/stat</c>'s <c>btime</c>: when the kernel booted, in UTC — the zero of every
/// process's <c>starttime</c>.</summary>
public static class BootTime
{
    public static Reading<DateTimeOffset> Parse(string procStat)
    {
        var line = ProcText.Lines(procStat).FirstOrDefault(l => l.StartsWith("btime ", StringComparison.Ordinal)) ?? string.Empty;
        return long.TryParse(line.Length > 6 ? line[6..].Trim() : string.Empty, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? Reading.Of(DateTimeOffset.FromUnixTimeSeconds(seconds))
            : Reading.Missing<DateTimeOffset>("/proc/stat has no btime line");
    }
}

/// <summary><c>/etc/passwd</c>, reduced to what the process table needs: uid → user name.</summary>
public static class Passwd
{
    public static IReadOnlyDictionary<int, string> Parse(string text) =>
        ProcText.Lines(text)
            .Select(l => l.Split(':'))
            .Where(f => f.Length >= 3 && int.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .GroupBy(f => int.Parse(f[2], CultureInfo.InvariantCulture))
            .ToDictionary(g => g.Key, g => g.First()[0]);
}
