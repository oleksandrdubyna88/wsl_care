using System.Text.Json;

using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;
using WslCare.Core.Json;

namespace WslCare.Core.Mcp;

/// <summary>One reading of one instance: its cumulative CPU ticks at an instant on both clocks (wall, monotonic ms since boot).</summary>
public sealed record McpCpuPoint(long CpuTicks, DateTimeOffset Wall, long MonotonicMs);

/// <summary>One instance as the ledger keeps it: its identity (pid AND start ticks, within the file's boot) and at most two points,
/// older first.</summary>
public sealed record McpCpuEntry(int Pid, long StartTicks, IReadOnlyList<McpCpuPoint> Points);

/// <summary><c>mcp-cpu.json</c> (plan E14 S1): one boot's instances.</summary>
public sealed record McpCpuFile(int SchemaVersion, string BootId, IReadOnlyList<McpCpuEntry> Entries)
{
    public static readonly McpCpuFile Empty = new(Core.SchemaVersion.Current, string.Empty, []);
}

/// <summary>One instance read now: its identity and its point.</summary>
public sealed record McpCpuReading(int Pid, long StartTicks, McpCpuPoint At);

/// <summary>The interval a ledger point must lie within to be a baseline: at least <paramref name="Min"/> old (shorter cannot see a
/// burst the window misses), at most <paramref name="Max"/> (longer stops describing now — review finding 3).</summary>
public sealed record McpCpuBounds(TimeSpan Min, TimeSpan Max);

/// <summary>Where a caller keeps its CPU ledger — a closed set, one place per caller (plan E14 S1, design 5).</summary>
public abstract record McpCpuLedgerPlace
{
    private McpCpuLedgerPlace()
    {
    }

    /// <summary>Root's, under the state directory: written by the root timer's <c>collect</c>, read by it and by <c>status</c> as root
    /// (which writes nothing — plan §15b #3), read back as root's state.</summary>
    public sealed record RootState(string Directory, string File, bool Writes) : McpCpuLedgerPlace;

    /// <summary>This account's own, under <c>$XDG_STATE_HOME/wsl-care</c>: read and written by an unprivileged <c>status</c>.</summary>
    public sealed record OwnState(string Directory, string File) : McpCpuLedgerPlace;

    /// <summary>No ledger: every instance is measured across the window.</summary>
    public sealed record None(string Reason) : McpCpuLedgerPlace;

    /// <summary>The ledger file, or empty for <see cref="None"/>.</summary>
    public string FileOrEmpty => this switch
    {
        RootState root => root.File,
        OwnState own => own.File,
        _ => string.Empty,
    };

    /// <summary><c>status</c>'s place: root's ledger read-only when root (every verb run as root is re-homed to the target user —
    /// <c>CliHost.ForThisMachine</c> — so root's "own" folder would be that user's: review finding 1), else this account's own.</summary>
    public static McpCpuLedgerPlace ForStatus(IHostPaths paths, bool root) => paths switch
    {
        LinuxHostPaths linux when root => new RootState(linux.StateDirectory, linux.Rules.Join(linux.StateDirectory, McpCpuLedger.FileName), Writes: false),
        LinuxHostPaths linux => new OwnState(linux.UserStateDirectory, linux.Rules.Join(linux.UserStateDirectory, McpCpuLedger.FileName)),
        _ => new None(McpServerCollector.WindowsNotYet),
    };

    /// <summary><c>collect</c>'s place: root's ledger when the run may record; none for a read-only run (<paramref name="readOnly"/>
    /// says why), which measures across the window and writes nothing.</summary>
    public static McpCpuLedgerPlace ForCollect(IHostPaths paths, bool mayRecord, string readOnly) => paths switch
    {
        LinuxHostPaths linux when mayRecord => new RootState(linux.StateDirectory, linux.Rules.Join(linux.StateDirectory, McpCpuLedger.FileName), Writes: true),
        LinuxHostPaths => new None(readOnly),
        _ => new None(McpServerCollector.WindowsNotYet),
    };
}

/// <summary>
/// The MCP servers' CPU ledger (plan E14 S1): per instance identity — boot id, pid, start ticks — its CPU ticks at up to two
/// instants, so the next sample measures the instance over the REAL interval since then rather than across one short window that
/// a periodic burst falls between (research/2026-10-07_evening_overload.md M1–M3).
/// </summary>
/// <remarks>
/// <para><b>Two points, so callers do not starve each other.</b> A new identity stores its point; when the newest stored point is at
/// least the minimum interval old it becomes the older one and now the newer; otherwise both stay. A caller a second after another
/// still finds the older point at least the minimum old.</para>
/// <para><b>The monotonic clock is the denominator</b> and decides a point's age: a wall clock that jumps after the host slept
/// would distort the rate. Whether the guest's monotonic clock stops while the Windows host sleeps is not measured (plan S8); if
/// it does not, a reading across a sleep is diluted, never inflated.</para>
/// <para>A missing, unreadable, foreign-owned, other-boot or malformed ledger is "no baseline": the window answers, never an error.
/// The file holds the listed instances only (at most <c>mcpServers.maxInstances</c> × 2 points) and is rewritten only when it
/// changed, atomically and private.</para>
/// </remarks>
public static class McpCpuLedger
{
    public const string FileName = "mcp-cpu.json";

    private const string Action = "mcp-cpu";

    /// <summary>The ledger <paramref name="place"/> holds; <see cref="McpCpuFile.Empty"/> when there is none or it cannot be read.</summary>
    public static McpCpuFile Read(IFileSystem files, McpCpuLedgerPlace place, int maxBytes) => place switch
    {
        McpCpuLedgerPlace.RootState root => Parse(files.ReadStateFile(root.File, maxBytes)),
        // Review finding 2: this account's own file, owner-checked and reached through no link — ReadStateFile trusts root's only.
        McpCpuLedgerPlace.OwnState own => Parse(files.ReadUserFile(own.File, maxBytes, RegularFiles.EffectiveUid(), own.Directory)),
        _ => McpCpuFile.Empty,
    };

    private static McpCpuFile Parse(FileReadResult read)
    {
        if (read is not FileReadResult.Content content)
        {
            return McpCpuFile.Empty;
        }

        try
        {
            return JsonSerializer.Deserialize(content.Bytes, WslCareJsonContext.Default.McpCpuFile) is { BootId.Length: > 0, Entries: not null } file ? file : McpCpuFile.Empty;
        }
        catch (JsonException)
        {
            return McpCpuFile.Empty;
        }
    }

    /// <summary>The newest point of this identity, in this boot, that lies within <paramref name="bounds"/> of <paramref name="now"/>
    /// and whose ticks are not above now's; <c>null</c> when there is none.</summary>
    public static McpCpuPoint? Baseline(McpCpuFile ledger, string bootId, McpCpuReading now, McpCpuBounds bounds) =>
        EntryOf(ledger, bootId, now)?.Points
            .Where(p => Usable(p, now.At, bounds) && AgeOf(p, now.At) >= bounds.Min)
            .MaxBy(p => p.MonotonicMs);

    /// <summary>The next ledger: each reading's identity with its points by the two-point rule; identities not read now, points
    /// outside the bounds and another boot's file dropped.</summary>
    public static McpCpuFile Next(McpCpuFile before, string bootId, IReadOnlyList<McpCpuReading> readings, McpCpuBounds bounds) =>
        new(Core.SchemaVersion.Current, bootId,
        [
            .. readings
                .Select(r => new McpCpuEntry(r.Pid, r.StartTicks, Points(EntryOf(before, bootId, r), r.At, bounds)))
                .OrderBy(e => e.Pid)
                .ThenBy(e => e.StartTicks),
        ]);

    private static IReadOnlyList<McpCpuPoint> Points(McpCpuEntry? was, McpCpuPoint now, McpCpuBounds bounds)
    {
        var kept = (was?.Points ?? []).Where(p => Usable(p, now, bounds)).OrderBy(p => p.MonotonicMs).ToList();
        return kept switch
        {
            [] => [now],
            [.., var newest] when AgeOf(newest, now) >= bounds.Min => [newest, now],
            [.., var older, var newer] => [older, newer],
            _ => kept,
        };
    }

    private static McpCpuEntry? EntryOf(McpCpuFile ledger, string bootId, McpCpuReading reading) =>
        bootId.Length > 0 && ledger.BootId == bootId
            ? ledger.Entries.FirstOrDefault(e => e.Pid == reading.Pid && e.StartTicks == reading.StartTicks)
            : null;

    /// <summary>Earlier than now on the monotonic clock, within the maximum, and not above now's ticks (review finding 7).</summary>
    private static bool Usable(McpCpuPoint point, McpCpuPoint now, McpCpuBounds bounds) =>
        point.MonotonicMs < now.MonotonicMs && AgeOf(point, now) <= bounds.Max && point.CpuTicks <= now.CpuTicks;

    private static TimeSpan AgeOf(McpCpuPoint point, McpCpuPoint now) => TimeSpan.FromMilliseconds(now.MonotonicMs - point.MonotonicMs);

    /// <summary>Writes <paramref name="next"/> where <paramref name="place"/> may write, only when it differs from
    /// <paramref name="before"/>; says whether this sample's readings are recorded and, when not, why.</summary>
    public static McpCpuBaseline Record(IFileSystem files, McpCpuLedgerPlace place, McpCpuFile before, McpCpuFile next) => place switch
    {
        McpCpuLedgerPlace.RootState { Writes: true } root => Write(files, root.Directory, root.File, before, next),
        McpCpuLedgerPlace.OwnState own => Write(files, own.Directory, own.File, before, next),
        McpCpuLedgerPlace.RootState root => McpCpuBaseline.NotRecorded(root.File, ReadOnlyRoot),
        McpCpuLedgerPlace.None none => McpCpuBaseline.NotRecorded(string.Empty, none.Reason),
        _ => throw new System.Diagnostics.UnreachableException("McpCpuLedgerPlace is a closed set"),
    };

    /// <summary>Why a root <c>status</c> records nothing.</summary>
    public const string ReadOnlyRoot = "status as root reads root's ledger and writes nothing (plan §15b #3); the root timer's full run records it";

    private static McpCpuBaseline Write(IFileSystem files, string directory, string file, McpCpuFile before, McpCpuFile next)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(next, WslCareJsonContext.Default.McpCpuFile);
        if (json.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(before, WslCareJsonContext.Default.McpCpuFile)))
        {
            return new McpCpuBaseline(file, true, string.Empty);
        }

        try
        {
            files.CreateDirectory(directory);
            return files.WritePrivateFileAtomically(file, json, new DeletionScope(directory, Action)) is DeletionVerdict.Refused refused
                ? McpCpuBaseline.NotRecorded(file, refused.Reason)
                : new McpCpuBaseline(file, true, string.Empty);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return McpCpuBaseline.NotRecorded(file, e.Message);
        }
    }
}
