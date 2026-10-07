using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using WslCare.Core.Actions.Engine;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Json;

namespace WslCare.Core.Archive;

/// <summary>Who holds a side's lease on the base: the host, its boot, the process and when it started (plan §15r D4).</summary>
public sealed record LeaseRecord(int V, string Host, string BootId, int Pid, long StartTicks, DateTimeOffset StartUtc, string RunId, DateTimeOffset SinceUtc);

/// <summary>The side's lease, taken — or why not.</summary>
public abstract record LeaseTaken
{
    private LeaseTaken()
    {
    }

    /// <summary>Held until <see cref="SideLease.Release"/>; <paramref name="Note"/> says when a dead holder's lease was taken over.</summary>
    public sealed record Held(BeneathFolder Folder, string Name, FileIdentity Identity, string Note) : LeaseTaken;

    /// <summary>Another live identity holds it (or it could not be judged): the run does not start.</summary>
    public sealed record Refused(string Why) : LeaseTaken;
}

/// <summary>
/// Plan §15r D4, review M1 — ONE writer per side's index files: a lease file per side on the base
/// (<c>&lt;base&gt;/.wsl-care/sides/&lt;side&gt;.lease</c>) made by an exclusive create, checked at every run start. A lease held by another
/// LIVE identity refuses the run naming it; one held by a dead process of THIS host and boot is taken over — removed only while its
/// bytes are still the ones judged — and said. Another host's lease is never judged dead from here.
/// </summary>
public static class SideLease
{
    private const string Folder = ".wsl-care";
    private const string Sides = "sides";
    private const int MaxLeaseBytes = 4096;

    /// <param name="settle">How the settle wait over an EMPTY lease waits (<see cref="Thread.Sleep(TimeSpan)"/> when omitted); a test
    /// records it or writes the lease meanwhile.</param>
    public static LeaseTaken Take(IArchiveFiles files, string baseFolder, string side, LeaseRecord me, IProcessTable processes, Action<TimeSpan>? settle = null)
    {
        var scope = new DeletionScope(baseFolder, "A13");
        if (files.OpenFolderBeneath(baseFolder, [Folder, Sides], scope) is not FolderBeneath.Ready { Folder: var folder })
        {
            return new LeaseTaken.Refused($"the lease folder {Folder}/{Sides} could not be opened in the base");
        }

        var name = side + ".lease";
        var first = Create(files, folder, name, me, scope, note: string.Empty);
        return first is LeaseTaken.Refused { Why: ExistsMark } ? TakeOver(files, folder, name, me, scope, processes, settle ?? Thread.Sleep) : Disposed(first, folder);
    }

    /// <summary>The lease removed — only the file this run created.</summary>
    public static void Release(IArchiveFiles files, LeaseTaken.Held held, string baseFolder)
    {
        _ = files.RemoveOwnCopy(held.Folder, held.Name, held.Identity, new DeletionScope(baseFolder, "A13"));
        held.Folder.Dispose();
    }

    private const string ExistsMark = "exists";

    private static LeaseTaken Create(IArchiveFiles files, BeneathFolder folder, string name, LeaseRecord me, DeletionScope scope, string note)
    {
        switch (files.CreateExclusive(folder, name, scope))
        {
            case ExclusiveFile.Created created:
                return Written(files, folder, name, me, scope, created) is { Length: > 0 } failed
                    ? new LeaseTaken.Refused($"the lease could not be written ({failed}); it was removed again")
                    : new LeaseTaken.Held(folder, name, created.Identity, note);
            case ExclusiveFile.Exists:
                return new LeaseTaken.Refused(ExistsMark);
            case ExclusiveFile.Refused refused:
                return new LeaseTaken.Refused($"the lease could not be created ({refused.Why})");
            default:
                return new LeaseTaken.Refused("the lease could not be created");
        }
    }

    /// <summary>The record written into the lease this run created; on a failure of the base (correctness review M8) the lease is
    /// removed again — never left empty to stop every later run. Empty when written.</summary>
    private static string Written(IArchiveFiles files, BeneathFolder folder, string name, LeaseRecord me, DeletionScope scope, ExclusiveFile.Created created)
    {
        try
        {
            using (created.Stream)
            {
                created.Stream.Write(JsonSerializer.SerializeToUtf8Bytes(me, WslCareJsonContext.Compact.LeaseRecord));
                created.Stream.Flush(flushToDisk: true);
            }

            return string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _ = files.RemoveOwnCopy(folder, name, created.Identity, scope);
            return e.Message;
        }
    }

    private static LeaseTaken TakeOver(IArchiveFiles files, BeneathFolder folder, string name, LeaseRecord me, DeletionScope scope, IProcessTable processes, Action<TimeSpan> settle)
    {
        if (Settled(files, folder, name, settle) is not FileReadResult.Content { Bytes: var bytes })
        {
            folder.Dispose();
            return new LeaseTaken.Refused($"the side's lease {name} exists and could not be read; nothing was done");
        }

        var holder = Parsed(bytes);
        var why = bytes.Length == 0 ? string.Empty : HolderLives(holder, me, processes);
        if (why.Length > 0)
        {
            folder.Dispose();
            return new LeaseTaken.Refused(why);
        }

        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return files.RemoveIfUnchanged(folder, name, sha, scope) is VerifiedRemoval.Removed or VerifiedRemoval.Gone
            ? Disposed(Create(files, folder, name, me, scope, TakenOver(bytes, holder)), folder)
            : Disposed(new LeaseTaken.Refused("the side's lease changed while it was taken over; nothing was done"), folder);
    }

    /// <summary>The lease's bytes — read AGAIN after the settle wait when they were empty: the creator writes its record right after
    /// its exclusive create, so a lease still empty is a run that died between the two (a kill at that point, seen in WSL).</summary>
    private static FileReadResult Settled(IArchiveFiles files, BeneathFolder folder, string name, Action<TimeSpan> settle)
    {
        var first = files.ReadCapped(folder, name, MaxLeaseBytes);
        if (first is not FileReadResult.Content { Bytes.Length: 0 })
        {
            return first;
        }

        settle(TimeSpan.FromMilliseconds(Tuning.Current.Int(ConfigKeys.Archive.LeaseSettleMilliseconds)));
        return files.ReadCapped(folder, name, MaxLeaseBytes);
    }

    private static string TakenOver(byte[] bytes, LeaseRecord? holder) => bytes.Length == 0
        ? "an empty lease (its run stopped between creating and writing it) was taken over"
        : $"the lease of a dead run ({Describe(holder)}) was taken over";

    /// <summary>Empty when the holder is dead on THIS host and boot; why the run must not start otherwise.</summary>
    public static string HolderLives(LeaseRecord? holder, LeaseRecord me, IProcessTable processes) =>
        holder is null ? "the side's lease exists and could not be parsed; remove it by hand once no run is active"
        : !string.Equals(holder.Host, me.Host, StringComparison.Ordinal) ? $"the side's lease is held by another host ({Describe(holder)}); two machines never write one side"
        : string.Equals(holder.BootId, me.BootId, StringComparison.Ordinal) ? SameBootHolder(holder, processes)
        : DeadOnEarlierBoot(holder, me);

    /// <summary>Another boot of this host: its processes are all gone — when both boots are known; otherwise it cannot be judged.</summary>
    private static string DeadOnEarlierBoot(LeaseRecord holder, LeaseRecord me) =>
        holder.BootId.Length > 0 && me.BootId.Length > 0 ? string.Empty : $"the side's lease is held by {Describe(holder)} and its boot cannot be compared";

    private static string SameBootHolder(LeaseRecord holder, IProcessTable processes) => processes.Lookup(holder.Pid) switch
    {
        ProcessLookup.Gone => string.Empty,
        ProcessLookup.Alive { StartTicks: long ticks } when ticks != holder.StartTicks => string.Empty,
        ProcessLookup.Alive alive when alive.StartTicks is null && alive.StartUtc != holder.StartUtc => string.Empty,
        ProcessLookup.Unknown unknown => $"the side's lease holder {Describe(holder)} cannot be inspected ({unknown.Reason})",
        _ => $"another archive run holds the side ({Describe(holder)})",
    };

    private static string Describe(LeaseRecord? holder) => holder is null ? "unknown" : $"host {holder.Host}, pid {holder.Pid}, run {holder.RunId}, since {holder.SinceUtc:O}";

    private static LeaseRecord? Parsed(byte[] bytes)
    {
        try
        {
            // Correctness review M2: a lease with a field missing (JSON reads it as null) is malformed, never judged.
            return JsonSerializer.Deserialize(Encoding.UTF8.GetString(bytes), WslCareJsonContext.Compact.LeaseRecord) is { Host: not null, BootId: not null, RunId: not null } whole ? whole : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A refusal disposes the lease folder; a held lease keeps it until <see cref="Release"/>.</summary>
    private static LeaseTaken Disposed(LeaseTaken taken, BeneathFolder folder)
    {
        if (taken is LeaseTaken.Refused)
        {
            folder.Dispose();
        }

        return taken;
    }
}
