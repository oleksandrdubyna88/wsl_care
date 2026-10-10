using System.Text.Json;
using System.Text.RegularExpressions;

using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;
using WslCare.Core.Json;

namespace WslCare.Core.Mcp;

/// <summary>One reading of one instance: its cumulative CPU ticks at an instant on both clocks (wall, monotonic ms since boot).</summary>
public sealed record McpCpuPoint(long CpuTicks, DateTimeOffset Wall, long MonotonicMs);

/// <summary>One instance as the ledger keeps it: its identity (pid AND start ticks, within the file's boot) and at most two points,
/// older first.</summary>
public sealed record McpCpuEntry(int Pid, long StartTicks, IReadOnlyList<McpCpuPoint> Points)
{
    /// <summary>E14 S2b: since when this identity has been busy without a log write over an unbroken chain of interval readings
    /// (A19's busy evidence) — the wall instant; <c>default</c> when it is not.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public DateTimeOffset BusySinceWall { get; init; }

    /// <summary>The same instant on the monotonic clock (milliseconds since boot); 0 when there is no streak.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public long BusySinceMs { get; init; }

    /// <summary>Whether a streak is recorded.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Busy => BusySinceMs > 0;
}

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

    /// <summary>The Windows binary's own, under its state directory (<c>%LOCALAPPDATA%\wsl-care</c>, E14 S7b.1): read and written by
    /// an unelevated <c>status</c>; an elevated one reads it and writes nothing (<paramref name="Writes"/> false) — it is the user's file.</summary>
    public sealed record WindowsState(string Directory, string File, bool Writes) : McpCpuLedgerPlace;

    /// <summary>No ledger: every instance is measured across the window.</summary>
    public sealed record None(string Reason) : McpCpuLedgerPlace;

    /// <summary>The ledger file, or empty for <see cref="None"/>.</summary>
    public string FileOrEmpty => this switch
    {
        RootState root => root.File,
        OwnState own => own.File,
        WindowsState windows => windows.File,
        _ => string.Empty,
    };

    /// <summary><c>status</c>'s place: root's ledger read-only when root (every verb run as root is re-homed to the target user —
    /// <c>CliHost.ForThisMachine</c> — so root's "own" folder would be that user's: review finding 1), else this account's own.</summary>
    public static McpCpuLedgerPlace ForStatus(IHostPaths paths, bool root) => paths switch
    {
        LinuxHostPaths linux when root => new RootState(linux.StateDirectory, linux.Rules.Join(linux.StateDirectory, McpCpuLedger.FileName), Writes: false),
        LinuxHostPaths linux => new OwnState(linux.UserStateDirectory, linux.Rules.Join(linux.UserStateDirectory, McpCpuLedger.FileName)),
        _ => new None(McpServerCollector.WindowsReadsItsOwn),
    };

    /// <summary>The Windows binary's <c>status</c> place (E14 S7b.1): its own state directory's ledger, written only when the process
    /// is not elevated.</summary>
    public static McpCpuLedgerPlace ForWindowsStatus(WindowsHostPaths paths, bool elevated) =>
        new WindowsState(paths.StateDirectory, paths.Rules.Join(paths.StateDirectory, McpCpuLedger.FileName), Writes: !elevated);

    /// <summary><c>collect</c>'s place: root's ledger when the run may record; none for a read-only run (<paramref name="readOnly"/>
    /// says why), which measures across the window and writes nothing.</summary>
    public static McpCpuLedgerPlace ForCollect(IHostPaths paths, bool mayRecord, string readOnly) => paths switch
    {
        LinuxHostPaths linux when mayRecord => new RootState(linux.StateDirectory, linux.Rules.Join(linux.StateDirectory, McpCpuLedger.FileName), Writes: true),
        LinuxHostPaths => new None(readOnly),
        _ => new None(McpServerCollector.WindowsReadsItsOwn),
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
public static partial class McpCpuLedger
{
    public const string FileName = "mcp-cpu.json";

    private const string Action = "mcp-cpu";

    /// <summary>The ledger <paramref name="place"/> holds; <see cref="McpCpuFile.Empty"/> when there is none or it cannot be read.</summary>
    public static McpCpuFile Read(IFileSystem files, McpCpuLedgerPlace place, int maxBytes) => place switch
    {
        McpCpuLedgerPlace.RootState root => Parse(files.ReadStateFile(root.File, maxBytes)),
        // Review finding 2: this account's own file, owner-checked and reached through no link — ReadStateFile trusts root's only.
        McpCpuLedgerPlace.OwnState own => Parse(files.ReadUserFile(own.File, maxBytes, RegularFiles.EffectiveUid(), own.Directory)),
        McpCpuLedgerPlace.WindowsState windows => Parse(files.ReadUserFile(windows.File, maxBytes, RegularFiles.EffectiveUid(), windows.Directory)),
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
            return JsonSerializer.Deserialize(content.Bytes, WslCareJsonContext.Compact.McpCpuFile) is { } file && WellFormed(file) ? file : McpCpuFile.Empty;
        }
        catch (JsonException)
        {
            return McpCpuFile.Empty;
        }
    }

    /// <summary>This schema, a boot, and no null where the records promise a value — the deserializer does not enforce the
    /// annotations, and a null entry or point would crash every later sample before the file could be rewritten (own code
    /// review, finding 1).</summary>
    private static bool WellFormed(McpCpuFile file) =>
        file is { SchemaVersion: Core.SchemaVersion.Current, BootId.Length: > 0, Entries: not null }
        && file.Entries.All(e => e is { Points: not null } && e.Points.All(p => p is { CpuTicks: >= 0, MonotonicMs: >= 0 }));

    /// <summary>A serialised entry with two points at their widest (compact JSON): what bounds how many entries fit under the
    /// read cap (own code review, finding 3 — a ledger past its own cap would read as empty for ever).</summary>
    // E14 S2b: 400, for the busy streak's two members (an entry at its widest is 369 bytes).
    public const int BytesPerEntry = 400;

    /// <summary>The most entries a ledger read back under <paramref name="maxBytes"/> can hold.</summary>
    public static int MaxEntries(int maxBytes) => maxBytes / BytesPerEntry;

    /// <summary>The newest point of this identity, in this boot, that lies within <paramref name="bounds"/> of <paramref name="now"/>
    /// and whose ticks are not above now's; <c>null</c> when there is none.</summary>
    public static McpCpuPoint? Baseline(McpCpuFile ledger, string bootId, McpCpuReading now, McpCpuBounds bounds) =>
        EntryOf(ledger, bootId, now)?.Points
            .Where(p => Usable(p, now.At, bounds) && AgeOf(p, now.At) >= bounds.Min)
            .MaxBy(p => p.MonotonicMs);

    /// <summary>The next ledger: each reading's identity with its points by the two-point rule; identities not read now, points
    /// outside the bounds and another boot's file dropped; at most <paramref name="maxEntries"/> — past it the OLDEST processes
    /// go, which only makes them "no baseline" (the window answers).</summary>
    public static McpCpuFile Next(McpCpuFile before, string bootId, IReadOnlyList<McpCpuReading> readings, McpCpuBounds bounds, int maxEntries) =>
        Next(before, bootId, readings, bounds, maxEntries, new HashSet<(int, long)>());

    /// <summary>The same, and each identity in <paramref name="busy"/> — busy without a log write over an interval reading NOW
    /// (plan E14 S2b) — keeps its entry's streak, or starts one at its reading; every other identity has none.</summary>
    public static McpCpuFile Next(McpCpuFile before, string bootId, IReadOnlyList<McpCpuReading> readings, McpCpuBounds bounds, int maxEntries, IReadOnlySet<(int Pid, long StartTicks)> busy) =>
        new(Core.SchemaVersion.Current, bootId,
        [
            .. readings
                .Select(r => Entry(EntryOf(before, bootId, r), r, bounds, busy.Contains((r.Pid, r.StartTicks))))
                .OrderByDescending(e => e.StartTicks)
                .Take(maxEntries)
                .OrderBy(e => e.Pid)
                .ThenBy(e => e.StartTicks),
        ]);

    /// <summary>One identity's next entry: its points, and its busy streak — kept from <paramref name="was"/> while it stays busy,
    /// started at this reading when it was not, none when it is not busy now.</summary>
    private static McpCpuEntry Entry(McpCpuEntry? was, McpCpuReading reading, McpCpuBounds bounds, bool busy)
    {
        var entry = new McpCpuEntry(reading.Pid, reading.StartTicks, Points(was, reading.At, bounds));
        return (busy, was) switch
        {
            (false, _) => entry,
            (true, { Busy: true }) => entry with { BusySinceWall = was.BusySinceWall, BusySinceMs = was.BusySinceMs },
            _ => entry with { BusySinceWall = reading.At.Wall, BusySinceMs = reading.At.MonotonicMs },
        };
    }

    /// <summary>How long this identity has been busy without a log write by <paramref name="ledger"/> (plan E14 S2b, A19's busy
    /// evidence): the shorter of the two clocks since its streak began — and only while the ledger is current, its newest point no
    /// older than <paramref name="max"/> (<c>mcpServers.cpuIntervalMaxMinutes</c>). Zero for another boot, another process, no streak
    /// or stale evidence.</summary>
    public static TimeSpan BusyFor(McpCpuFile ledger, string bootId, int pid, long startTicks, McpCpuPoint now, TimeSpan max) =>
        EntryOf(ledger, bootId, new McpCpuReading(pid, startTicks, now)) is { Busy: true, Points: [.., var newest] } entry
        && newest.MonotonicMs <= now.MonotonicMs && AgeOf(newest, now) <= max && entry.BusySinceMs <= now.MonotonicMs
            ? Shorter(now.Wall - entry.BusySinceWall, TimeSpan.FromMilliseconds(now.MonotonicMs - entry.BusySinceMs))
            : TimeSpan.Zero;

    private static TimeSpan Shorter(TimeSpan wall, TimeSpan monotonic) => wall < monotonic ? wall : monotonic;

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
    /// <paramref name="before"/> — first sweeping the ledger's own temp files that a write which died left behind (older than
    /// <see cref="McpCpuSweep.OrphanAfter"/>; coai plan round finding 3) — and says whether this sample's readings are recorded and,
    /// when not, why.</summary>
    public static McpCpuBaseline Record(IFileSystem files, McpCpuLedgerPlace place, McpCpuFile before, McpCpuFile next, McpCpuSweep sweep) => place switch
    {
        McpCpuLedgerPlace.RootState { Writes: true } root => Write(files, root.Directory, root.File, before, next, sweep),
        McpCpuLedgerPlace.OwnState own => Write(files, own.Directory, own.File, before, next, sweep),
        McpCpuLedgerPlace.WindowsState { Writes: true } windows => Write(files, windows.Directory, windows.File, before, next, sweep),
        McpCpuLedgerPlace.WindowsState windows => McpCpuBaseline.NotRecorded(windows.File, ReadOnlyElevated),
        McpCpuLedgerPlace.RootState root => McpCpuBaseline.NotRecorded(root.File, ReadOnlyRoot),
        McpCpuLedgerPlace.None none => McpCpuBaseline.NotRecorded(string.Empty, none.Reason),
        _ => throw new System.Diagnostics.UnreachableException("McpCpuLedgerPlace is a closed set"),
    };

    /// <summary>Why a root <c>status</c> records nothing.</summary>
    public const string ReadOnlyRoot = "status as root reads root's ledger and writes nothing (plan §15b #3); the root timer's full run records it";

    /// <summary>Why an elevated Windows <c>status</c> records nothing (E14 S7b.1).</summary>
    public const string ReadOnlyElevated = "an elevated status reads the user's ledger and writes nothing; the user's own status records it";

    private static McpCpuBaseline Write(IFileSystem files, string directory, string file, McpCpuFile before, McpCpuFile next, McpCpuSweep sweep)
    {
        // The cadence consultation (2026-10-08): the atomic writer resolves a link and replaces its target, and a sibling target
        // passes the folder's scope — so a ledger that is a link is refused here, as its read already refuses it.
        if (files.ReadLink(file) is LinkReadResult.Target link)
        {
            return McpCpuBaseline.NotRecorded(file, $"{file} is a link (to {link.Path}); a link is never written through");
        }

        var json = Serialise(next);
        try
        {
            SweepOrphans(files, directory, sweep);
            if (json.AsSpan().SequenceEqual(Serialise(before)))
            {
                return new McpCpuBaseline(file, true, string.Empty);
            }

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

    /// <summary>The ledger as written: one line (<see cref="WslCareJsonContext.Compact"/>), so <see cref="BytesPerEntry"/> holds.</summary>
    public static byte[] Serialise(McpCpuFile ledger) => JsonSerializer.SerializeToUtf8Bytes(ledger, WslCareJsonContext.Compact.McpCpuFile);

    /// <summary>Removes this ledger's temp files (<c>mcp-cpu.json.&lt;32 hex&gt;.tmp</c>, the atomic writer's exact shape) last written
    /// more than <see cref="McpCpuSweep.OrphanAfter"/> ago — a younger one may be a concurrent writer's, about to be renamed. Every
    /// delete goes through the deletion policy inside the ledger's own folder; a refused one stays, and is retried next time.</summary>
    private static void SweepOrphans(IFileSystem files, string directory, McpCpuSweep sweep)
    {
        foreach (var temp in files.ListFiles(directory).Where(p => OrphanName().IsMatch(Path.GetFileName(p))))
        {
            if (files.FileSize(temp) is FileSizeResult.Measured { ModifiedAt: var at } && sweep.Now - at >= sweep.OrphanAfter)
            {
                TryDelete(files, temp, directory);
            }
        }
    }

    /// <summary>One orphan removed, or left for the next write: the sweep is housekeeping, and a file held open elsewhere must not
    /// stop the ledger being written (coai code round 2026-10-08, finding 2).</summary>
    private static void TryDelete(IFileSystem files, string temp, string directory)
    {
        try
        {
            files.DeleteFile(temp, new DeletionScope(directory, Action));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left in place; the next write tries again. Nothing else depends on it being gone.
        }
    }

    [GeneratedRegex(@"^mcp-cpu\.json\.[0-9a-f]{32}\.tmp$", RegexOptions.CultureInvariant)]
    private static partial Regex OrphanName();
}

/// <summary>When a ledger temp file counts as left by a write that died: older than <paramref name="OrphanAfter"/> at
/// <paramref name="Now"/>. The collector passes the minimum interval — a write takes milliseconds, so a temp file older than the
/// shortest interval a baseline needs is nobody's (coai plan round finding 3).</summary>
public sealed record McpCpuSweep(DateTimeOffset Now, TimeSpan OrphanAfter);
