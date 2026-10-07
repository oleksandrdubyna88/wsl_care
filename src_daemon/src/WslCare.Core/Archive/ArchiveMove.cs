using System.Security.Cryptography;
using System.Text;

using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;

namespace WslCare.Core.Archive;

/// <summary>The protocol's own steps the fault seam is asked at (plan §15r *Test plan*, the 14 kill points — the seam's steps are the
/// rest: <see cref="ArchiveFileStep"/>).</summary>
public static class MoveSteps
{
    public const string Intent = "intent";
    public const string CopyChunk = "copy-chunk";
    public const string FinalFlushed = "final-flushed";
    public const string BetweenFiles = "between-files";
    public const string IndexFlushed = "index-flushed";
    public const string QuarantineStart = "quarantine-start";
    public const string FoldersStart = "folders-start";
    public const string CloseStart = "close-start";
    public const string Closed = "closed";

    public static IReadOnlyList<string> All { get; } = [Intent, CopyChunk, FinalFlushed, BetweenFiles, IndexFlushed, QuarantineStart, FoldersStart, CloseStart, Closed];
}

/// <summary>Everything one run's moves share: the seam, the base, this side, the run, the index key, the clock and the fault seam.</summary>
/// <param name="BaseFolder">The base as this process reaches it (its real path).</param>
/// <param name="Step">Asked at each protocol step (<see cref="MoveSteps"/>); throws or kills in a test, does nothing in a run.</param>
/// <param name="Progress">Told the bytes of every chunk copied (false) and of every file done (true) — what the budget's rate and the
/// progress lines read.</param>
public sealed record MoveContext(IArchiveFiles Files, string BaseFolder, string Side, string RunId, byte[] Key, TimeProvider Clock, TimeZoneInfo Zone, InflightBook Book, Action<string> Step, Action<long, bool> Progress)
{
    public DeletionScope BaseScope => new(BaseFolder, "A13");

    /// <summary>Names and stats only — what phase 2's git re-check reads (owner rule 2026-10-07).</summary>
    public required IFileSystem Stats { get; init; }

    /// <summary>The target user's home — phase 2 judges the agent's folder against it as the selection does (security review M-1).</summary>
    public required string Home { get; init; }

    /// <summary>This run's open-file scan — phase 2 never touches a unit an agent may be working on (security review M-2).</summary>
    public required InUseView InUse { get; init; }

    /// <summary>A path as the open-file scan spells it (the distribution's spelling under a sandbox root).</summary>
    public required Func<string, string> Distro { get; init; }
}

/// <summary>What phase 1 did with one unit — a closed set.</summary>
public abstract record CopyOutcome
{
    private CopyOutcome()
    {
    }

    /// <summary>Copied (or found already there, equal), verified, indexed; the entry waits for phase 2.</summary>
    public sealed record Archived(InflightEntry Entry, long Bytes, int Files) : CopyOutcome;

    /// <summary>A file of it was removed by the agent before it could be copied — counted, never a failure; nothing is indexed.</summary>
    public sealed record GoneAtSource(string Why) : CopyOutcome;

    /// <summary>Kept where it is this run, and why (a source refused, a destination unreachable).</summary>
    public sealed record Skipped(string Why) : CopyOutcome;

    /// <summary>The run must STOP (coai G1: a second read-back mismatch; or the index could not be written): a base that lies is not
    /// written again.</summary>
    public sealed record Stop(string Kind, string Why) : CopyOutcome;
}

/// <summary>
/// Plan §15r D2 phase 1 — ONE unit copied, verified and indexed, touching nothing at the source: the intent first (in-flight
/// <c>copying</c>), each file streamed from its original name through no link into an exclusive create under
/// <c>&lt;agent&gt;/&lt;yyyy&gt;/&lt;MM&gt;/&lt;side&gt;/</c>, hashed as it is read, flushed, its last write kept, read back and hashed again (one retry,
/// then the run stops); the folder flushed; ONE <c>archived</c> line in the month index, flushed; then the entry <c>archived</c>.
/// </summary>
public static class ArchiveCopy
{
    /// <summary>The widest <c>~N</c> a name takes before the unit is skipped — the suffix's two digits (a format, group C).</summary>
    private const int MaxSuffix = 99;

    public static CopyOutcome Copy(MoveContext c, string agent, string under, UnitFound unit)
    {
        var intent = new InflightEntry(ArchiveIndex.EntryIdOf(c.Side, agent, unit.Key, []), agent, under, unit.Key, unit.Month, InflightStates.Copying, c.RunId, DateTimeOffset.MinValue, unit.Files.Count, string.Empty);
        if (c.Book.Put(intent) is { Length: > 0 } unwritten)
        {
            return new CopyOutcome.Stop(StopKinds.StateWrite, $"the in-flight file could not be written ({unwritten})");
        }

        c.Step(MoveSteps.Intent);
        var copied = new List<IndexFile>();
        foreach (var file in unit.Files)
        {
            var one = CopyFile(c, agent, under, unit.Month, file);
            if (one is not FileCopied done)
            {
                c.Book.Drop(intent.EntryId);
                return ((FileFailed)one).Outcome;
            }

            copied.Add(done.Indexed);
            c.Progress(0, true);
            c.Step(MoveSteps.BetweenFiles);
        }

        return Indexed(c, agent, unit, intent, copied);
    }

    /// <summary>One file's answer: copied (or reused) with its index row, or the unit's outcome.</summary>
    private abstract record FileResult;

    private sealed record FileCopied(IndexFile Indexed) : FileResult;

    private sealed record FileFailed(CopyOutcome Outcome) : FileResult;

    private static FileResult CopyFile(MoveContext c, string agent, string under, string month, UnitFile file)
    {
        var levels = Levels(agent, month, c.Side, Folders(file.Relative));
        if (c.Files.OpenFolderBeneath(c.BaseFolder, levels, c.BaseScope) is not FolderBeneath.Ready { Folder: var folder })
        {
            return new FileFailed(new CopyOutcome.Skipped($"the destination of {file.Relative} could not be opened in the base"));
        }

        using (folder)
        {
            return Placed(c, under, file, folder);
        }
    }

    /// <summary>The first free name — the file's own, then <c>~2</c>, <c>~3</c> … — or one that already holds the same bytes.</summary>
    private static FileResult Placed(MoveContext c, string under, UnitFile file, BeneathFolder folder)
    {
        var name = Path.GetFileName(file.Relative);
        for (var n = 1; n <= MaxSuffix; n++)
        {
            var candidate = n == 1 ? name : Suffixed(name, n);
            var result = Attempt(c, under, file, folder, candidate);
            if (result is not Taken)
            {
                return result;
            }
        }

        return new FileFailed(new CopyOutcome.Skipped($"{file.Relative}: every name up to ~{MaxSuffix} is taken by other bytes"));
    }

    /// <summary>The candidate name already holds OTHER bytes: try the next one.</summary>
    private sealed record Taken : FileResult;

    private static FileResult Attempt(MoveContext c, string under, UnitFile file, BeneathFolder folder, string name) =>
        c.Files.CreateExclusive(folder, name, c.BaseScope) switch
        {
            ExclusiveFile.Created created => Written(c, under, file, folder, name, created, retried: false),
            ExclusiveFile.Exists => Existing(c, under, file, folder, name),
            ExclusiveFile.Refused refused => new FileFailed(new CopyOutcome.Skipped($"{file.Relative} could not be created in the base ({refused.Why})")),
            _ => new FileFailed(new CopyOutcome.Skipped(file.Relative)),
        };

    /// <summary>A file already at the name: reused when it holds the source's bytes (a crashed earlier run's copy), else taken.</summary>
    private static FileResult Existing(MoveContext c, string under, UnitFile file, BeneathFolder folder, string name)
    {
        var source = SourceHash(c, under, file);
        if (source is not FileHash.Hashed { Sha256: var sha, Length: var length })
        {
            return new FileFailed(Unopened(source, file));
        }

        return c.Files.ReadBack(folder, name) is FileHash.Hashed there && there.Sha256 == sha && there.Length == length
            ? new FileCopied(new IndexFile(file.Relative, ArchivedPath(file.Relative, name), length, sha, file.LastWriteUtc))
            : new Taken();
    }

    private static FileHash SourceHash(MoveContext c, string under, UnitFile file) => c.Files.OpenSource(under, Path.Combine(under, file.Relative)) switch
    {
        SourceOpen.Opened opened => Hashed(opened.Stream),
        SourceOpen.Gone => new FileHash.Gone(),
        SourceOpen.Refused refused => new FileHash.Unreadable(refused.Why),
        _ => new FileHash.Unreadable(file.Relative),
    };

    private static FileHash Hashed(Stream stream)
    {
        using (stream)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[CopyBuffer];
            long length = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                length += read;
            }

            return new FileHash.Hashed(Convert.ToHexStringLower(sha.GetHashAndReset()), length);
        }
    }

    private static CopyOutcome Unopened(FileHash source, UnitFile file) => source is FileHash.Gone
        ? new CopyOutcome.GoneAtSource($"{file.Relative} was removed by the agent before it could be archived")
        : new CopyOutcome.Skipped($"{file.Relative} could not be read ({(source as FileHash.Unreadable)?.Why})");

    /// <summary>The bytes streamed from the source into the new file, hashed as read; flushed; the source's last write set; then
    /// the folder flushed and the file read back. A mismatch removes OUR file and retries once; the second stops the run.</summary>
    private static FileResult Written(MoveContext c, string under, UnitFile file, BeneathFolder folder, string name, ExclusiveFile.Created created, bool retried)
    {
        FileHash copied;
        try
        {
            copied = Streamed(c, under, file, created);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Correctness review M8: the base failed while the copy was written — our partial copy goes, and the run stops.
            _ = c.Files.RemoveOwnCopy(folder, name, created.Identity, c.BaseScope);
            return new FileFailed(new CopyOutcome.Stop(StopKinds.BaseFailed, $"{file.Relative}: the base failed while its copy was written ({e.Message}); the partial copy was removed and the run stops (nothing was removed at the source)"));
        }

        if (copied is not FileHash.Hashed { Sha256: var sha, Length: var length })
        {
            _ = c.Files.RemoveOwnCopy(folder, name, created.Identity, c.BaseScope);
            return new FileFailed(Unopened(copied, file));
        }

        return Verified(c, folder, name, sha, length)
            ? new FileCopied(new IndexFile(file.Relative, ArchivedPath(file.Relative, name), length, sha, file.LastWriteUtc))
            : Mismatch(c, under, file, folder, name, created, retried);
    }

    private static FileResult Mismatch(MoveContext c, string under, UnitFile file, BeneathFolder folder, string name, ExclusiveFile.Created created, bool retried)
    {
        _ = c.Files.RemoveOwnCopy(folder, name, created.Identity, c.BaseScope);
        _ = c.Files.FlushFolder(folder);
        if (retried)
        {
            return new FileFailed(new CopyOutcome.Stop(StopKinds.Verification, $"{file.Relative}: its copy read back different twice — the base does not keep what it is given; the run stops (nothing was removed)"));
        }

        return c.Files.CreateExclusive(folder, name, c.BaseScope) is ExclusiveFile.Created again
            ? Written(c, under, file, folder, name, again, retried: true)
            : new FileFailed(new CopyOutcome.Stop(StopKinds.Verification, $"{file.Relative}: its copy read back different and could not be written again; the run stops"));
    }

    private static FileHash Streamed(MoveContext c, string under, UnitFile file, ExclusiveFile.Created created)
    {
        using var target = created.Stream;
        var open = c.Files.OpenSource(under, Path.Combine(under, file.Relative));
        if (open is not SourceOpen.Opened opened)
        {
            return open is SourceOpen.Refused refused ? new FileHash.Unreadable(refused.Why) : new FileHash.Gone();
        }

        using var source = opened.Stream;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[CopyBuffer];
        long length = 0;
        int read;
        while ((read = SourceChunk(source, buffer)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            target.Write(buffer, 0, read);
            length += read;
            c.Progress(read, false);
            c.Step(MoveSteps.CopyChunk);
        }

        if (read < 0)
        {
            return new FileHash.Unreadable($"{file.Relative} could not be read to its end");
        }

        target.Flush(flushToDisk: true);
        File.SetLastWriteTimeUtc(target.SafeFileHandle, opened.LastWriteUtc.UtcDateTime);
        c.Step(MoveSteps.FinalFlushed);
        return new FileHash.Hashed(Convert.ToHexStringLower(sha.GetHashAndReset()), length);
    }

    /// <summary>One chunk of the SOURCE; -1 when reading it failed — the source's failure, never the base's (correctness review M8).</summary>
    private static int SourceChunk(Stream source, byte[] buffer)
    {
        try
        {
            return source.Read(buffer, 0, buffer.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    private static bool Verified(MoveContext c, BeneathFolder folder, string name, string sha, long length) =>
        c.Files.FlushFolder(folder) is FolderFlush.Done
        && c.Files.ReadBack(folder, name) is FileHash.Hashed back && back.Sha256 == sha && back.Length == length;

    /// <summary>The <c>archived</c> line for the unit, appended to its month index and flushed; then the entry is <c>archived</c>.</summary>
    private static CopyOutcome Indexed(MoveContext c, string agent, UnitFound unit, InflightEntry intent, IReadOnlyList<IndexFile> files)
    {
        var now = c.Clock.GetUtcNow();
        var entryId = ArchiveIndex.EntryIdOf(c.Side, agent, unit.Key, files.Select(f => f.Sha256));
        var line = new IndexLine(ArchiveIndex.SchemaVersion, ArchiveIndex.Events.Archived, entryId, agent, c.Side, unit.Key, unit.Month, now, c.Zone.Id, c.RunId, files, string.Empty);
        var appended = AppendLine(c, agent, unit.Month, line);
        if (appended.Length > 0)
        {
            c.Book.Drop(intent.EntryId);
            return new CopyOutcome.Stop(StopKinds.IndexWrite, $"the month index could not be written ({appended}); the copies stay without an index line and authorise nothing");
        }

        c.Step(MoveSteps.IndexFlushed);
        var archived = intent with { EntryId = entryId, State = InflightStates.Archived, ArchivedAtUtc = now };
        c.Book.Replace(intent.EntryId, archived);
        return new CopyOutcome.Archived(archived, files.Sum(f => f.Bytes), files.Count);
    }

    /// <summary>One line appended to the index of <paramref name="agent"/>'s <paramref name="month"/> on this side, flushed; empty when it
    /// is on the disk, why not otherwise.</summary>
    public static string AppendLine(MoveContext c, string agent, string month, IndexLine line)
    {
        if (c.Files.OpenFolderBeneath(c.BaseFolder, Levels(agent, month, c.Side, []), c.BaseScope) is not FolderBeneath.Ready { Folder: var folder })
        {
            return "its folder could not be opened";
        }

        using (folder)
        {
            return c.Files.AppendDurably(folder, ArchiveIndex.FileName, ArchiveIndex.Line(line, c.Key), c.BaseScope) switch
            {
                DurableAppend.Appended => string.Empty,
                DurableAppend.Refused refused => refused.Why,
                DurableAppend.Failed failed => failed.Why,
                _ => "unknown",
            };
        }
    }

    /// <summary>The levels below the base: the agent, the month's year and month, this side, then the file's own folders.</summary>
    public static IReadOnlyList<string> Levels(string agent, string month, string side, IReadOnlyList<string> folders) =>
        [agent, .. month.Split('/'), side, .. folders];

    /// <summary>The folders of a relative path (<c>projects/p/s.jsonl</c> → <c>projects</c>, <c>p</c>).</summary>
    public static IReadOnlyList<string> Folders(string relative)
    {
        var parts = relative.Split('/');
        return parts[..^1];
    }

    /// <summary>The file's path under the side folder with the name it got.</summary>
    private static string ArchivedPath(string relative, string name) =>
        string.Join('/', [.. Folders(relative), name]);

    private static string Suffixed(string name, int n)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? $"{name}~{n}" : $"{name[..dot]}~{n}{name[dot..]}";
    }

    private static int CopyBuffer => Tuning.Current.Int(ConfigKeys.Archive.CopyBufferKib) * 1024;

    /// <summary>The hash of a stream's bytes; the stream disposed.</summary>
    public static string Sha256Of(Stream stream) => Hashed(stream) is FileHash.Hashed hashed ? hashed.Sha256 : string.Empty;

    /// <summary>The hash of a text — what an entry's files are named by in a test.</summary>
    public static string Sha256Of(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>The in-flight file kept in memory and written after every change (plan §15r D2 step 3: the intent before any byte).</summary>
public sealed class InflightBook(ArchiveState state)
{
    private readonly List<InflightEntry> _entries = [.. state.Inflight().Entries];

    public IReadOnlyList<InflightEntry> Entries => _entries;

    /// <summary>Adds or replaces the entry with this id; empty when written.</summary>
    public string Put(InflightEntry entry)
    {
        _entries.RemoveAll(e => e.EntryId == entry.EntryId);
        _entries.Add(entry);
        return Save();
    }

    public string Replace(string entryId, InflightEntry entry)
    {
        _entries.RemoveAll(e => e.EntryId == entryId);
        _entries.Add(entry);
        return Save();
    }

    public string Drop(string entryId)
    {
        _entries.RemoveAll(e => e.EntryId == entryId);
        return Save();
    }

    private string Save() => state.WriteInflight(new InflightFile(ArchiveState.Version, [.. _entries]));
}
