using WslCare.Core.Files;

namespace WslCare.Core.Mcp;

/// <summary>The ledger a Windows sample may use, and the ledger lock it holds while it does — released by <see cref="Dispose"/>, after
/// the write. A sample that holds no lock (a read-only place, no ledger, a lock held by another writer) holds nothing.</summary>
public sealed record LedgerLease(WindowsCpuLedger Ledger, IDisposable Handle) : IDisposable
{
    public void Dispose() => Handle.Dispose();
}

/// <summary>
/// The Windows ledger's lock (WR-D4 of PLAN_windows_runner.md §3.2, §3.9; coai plan round 8138f140, finding 1): two writers that both
/// read before either writes would each replace the file and drop the other's sample — the atomic write does not serialise them. So
/// every Windows ledger WRITER (the Windows <c>watch</c> and the Windows <c>status</c>, both through <see cref="WindowsMcpCollector"/>)
/// does its read-modify-write under ONE lock, <c>mcp-cpu.lock</c> beside the ledger, taken with <see cref="IFileSystem.TryLockExclusive"/>
/// and never waited on. The file is read only AFTER the lock is held. When another writer holds it, this sample keeps no ledger: it
/// answers from the window and records nothing, and says why — so no write ever replaces a sample it did not read. A lost sample only
/// makes the next interval longer (still under <c>mcpServers.cpuIntervalMaxMinutes</c> at a 5-minute period).
/// </summary>
/// <remarks>An elevated status (<see cref="McpCpuLedgerPlace.WindowsState.Writes"/> false) writes nothing, so it takes no lock: the
/// atomic write means a reader never sees half a file. The lock file stays on disk, empty, as the run lock's does — holding is the open.</remarks>
public static class McpCpuLedgerLock
{
    public const string FileName = "mcp-cpu.lock";

    /// <summary>The lock beside <paramref name="ledgerFile"/>, in the same folder and spelled with the same separator.</summary>
    public static string Beside(string ledgerFile) =>
        ledgerFile.EndsWith(McpCpuLedger.FileName, StringComparison.Ordinal) ? ledgerFile[..^McpCpuLedger.FileName.Length] + FileName : ledgerFile + ".lock";

    /// <summary>The lease for one sample: a writing Windows ledger with its lock held, or — the lock held elsewhere — no ledger and why.</summary>
    public static LedgerLease Take(WindowsCpuLedger ledger) => ledger switch
    {
        WindowsCpuLedger.Kept { Place: McpCpuLedgerPlace.WindowsState { Writes: true } place } kept => Take(kept, place),
        _ => new LedgerLease(ledger, Nothing.Held),
    };

    private static LedgerLease Take(WindowsCpuLedger.Kept kept, McpCpuLedgerPlace.WindowsState place) => TryLock(kept.Files, place.LockFile) switch
    {
        ExclusiveLock.Held held => new LedgerLease(kept, held.Handle),
        ExclusiveLock.Busy busy => new LedgerLease(new WindowsCpuLedger.None(HeldElsewhere(busy.Reason)), Nothing.Held),
        _ => throw new System.Diagnostics.UnreachableException("ExclusiveLock is a closed set"),
    };

    /// <summary>The lock, or why not — a folder this process may not open is "not held", never a crash of the sample.</summary>
    private static ExclusiveLock TryLock(IFileSystem files, string lockFile)
    {
        try
        {
            return files.TryLockExclusive(lockFile);
        }
        catch (UnauthorizedAccessException e)
        {
            return new ExclusiveLock.Busy($"{lockFile} cannot be opened ({e.Message})");
        }
    }

    /// <summary>Why a sample that found the lock held records nothing.</summary>
    public static string HeldElsewhere(string reason) =>
        $"another writer holds the ledger lock ({reason}): this sample is measured across the window and records nothing, so it replaces no sample it did not read";

    /// <summary>The handle of a lease that holds no lock.</summary>
    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Held = new();

        public void Dispose()
        {
            // Nothing is held, so nothing is released.
        }
    }
}
