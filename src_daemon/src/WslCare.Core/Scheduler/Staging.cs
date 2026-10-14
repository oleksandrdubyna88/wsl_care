using System.Text.Json;

using WslCare.Core.Archive;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;
using WslCare.Core.Json;

namespace WslCare.Core.Scheduler;

/// <summary>
/// The staged copies made and removed (WR-D2 of PLAN_windows_runner.md §3.2, §3.10, §4): <c>tasks stage</c> copies this process's
/// own exe to <c>&lt;stagingRoot&gt;\&lt;digest&gt;\wsl-care.exe</c>; <see cref="Sweep"/> and <see cref="Remove"/> take the others away.
/// </summary>
/// <remarks>
/// <para>Every file act goes through the archive's seam (<see cref="IArchiveFiles"/>: the no-link source open, the folder opened level
/// by level and held, the exclusive no-replace create, the read-back past the cache, the removal of THIS stage's own copy by its
/// identity, the non-recursive empty-folder removal) or <see cref="IFileSystem"/>'s policy-judged writes and deletes — each judged by
/// the deletion policy inside the staging root (<see cref="Action"/>); nothing here deletes or creates by itself.</para>
/// <para>Never over an existing folder in place: a digest folder that is already there is reused when it verifies
/// (<see cref="StagedCopies.Check"/>, the render's own check); one that does not verify (an interrupted stage) is removed whole
/// through the deletion policy and staged fresh — unless its exe is in use, which refuses and names it. Nothing is ever written into
/// an existing folder.</para>
/// </remarks>
public static partial class Staging
{
    /// <summary>The deletion policy's name for every write and removal here.</summary>
    public const string Action = "tasks-stage";

    /// <summary>Copies <see cref="StageInput.Source"/> into its digest folder, reads it back, records it, then sweeps the other
    /// digest folders — or reuses a verified copy of the same digest, or refuses (no <c>stage.json</c>, nothing swept).</summary>
    /// <remarks>Under THE run lock, taken without waiting (the review of WR-D2, m4): two stages of one digest are serialised, and a
    /// stage never runs beside a sweep. A held lock answers <see cref="StageOutcomes.Busy"/> — nothing was done; try again.</remarks>
    public static StageReport Stage(StageInput input)
    {
        if (input.Files.TryLockExclusive(input.Place.RunLockFile) is not ExclusiveLock.Held held)
        {
            return new StageReport(Core.SchemaVersion.Current, StageOutcomes.Busy, BusyReason(input.Place), string.Empty, string.Empty, string.Empty, input.Source, [], [], []);
        }

        using (held.Handle)
        {
            var hashed = SourceDigest(input);
            return hashed.Problem.Length > 0 ? Refused(input, string.Empty, hashed.Problem) : AtName(input, hashed.Digest);
        }
    }

    private static string BusyReason(StagingPlace place) =>
        $"another run holds the run lock {place.RunLockFile}; nothing was done — try again (it is never waited on)";

    /// <summary>What the staging root holds at the digest's name, by its ORDINAL name from the listing (the review of WR-D2, M1: a
    /// junction there would lead every act through it to another folder).</summary>
    private enum NameAt
    {
        Free,
        Folder,
        Link,
        File,

        /// <summary>An entry whose name equals the digest only when case is ignored — on NTFS the same name, never a staging folder.</summary>
        OtherCase,
    }

    private static NameAt WhatIsAt(IFileSystem files, string root, string digest) =>
        files.ListEntries(root)
            .Where(e => string.Equals(e.Name, digest, StringComparison.OrdinalIgnoreCase))
            .Select(e => KindOf(e, digest))
            .DefaultIfEmpty(NameAt.Free)
            .First();

    private static NameAt KindOf(FileEntry entry, string digest) => entry switch
    {
        _ when !string.Equals(entry.Name, digest, StringComparison.Ordinal) => NameAt.OtherCase,
        { Kind: EntryKind.Directory } => NameAt.Folder,
        { Kind: EntryKind.Link } => NameAt.Link,
        _ => NameAt.File,
    };

    /// <summary>A real folder at the name is judged as an existing copy; a link, a file or another spelling is refused and named,
    /// never followed and never replaced; a free name is staged.</summary>
    private static StageReport AtName(StageInput input, string digest) => WhatIsAt(input.Files, input.Place.Root, digest) switch
    {
        NameAt.Free => Created(input, digest),
        NameAt.Folder => Existing(input, digest, replace: true),
        NameAt.Link => Refused(input, digest, $"{FolderOf(input, digest)} is a link at the digest name, never followed and never replaced; nothing was staged"),
        NameAt.File => Refused(input, digest, $"{FolderOf(input, digest)} is a file in the way of the digest folder; it is never replaced; nothing was staged"),
        _ => Refused(input, digest, $"the staging root holds the digest name in another case ({FolderOf(input, digest)}); it is never used or replaced; nothing was staged"),
    };

    private static DeletionScope Scope(StagingPlace place) => new(place.Root, Action);

    private static string FolderOf(StageInput input, string digest) => PathRules.Windows.Join(input.Place.Root, digest);

    private static string ExeOf(StageInput input, string digest) => PathRules.Windows.Join(input.Place.Root, digest, StagedCopies.ExeName);

    /// <summary>The source's digest — the name of its folder — or why it could not be read.</summary>
    private sealed record SourceHash(string Digest, string Problem);

    private static SourceHash SourceDigest(StageInput input) =>
        input.Source.Length == 0 || !PathRules.Windows.IsAbsolute(input.Source)
            ? new SourceHash(string.Empty, "this process's own path is not known, so there is nothing to stage")
            : input.Archive.OpenSource(PathRules.Windows.Parent(input.Source), input.Source) switch
            {
                SourceOpen.Opened opened => Digested(opened.Stream, input.Source),
                SourceOpen.Gone => new SourceHash(string.Empty, $"{input.Source} is not there to copy"),
                SourceOpen.Refused refused => new SourceHash(string.Empty, $"{input.Source} is not copied: {refused.Why}"),
                _ => new SourceHash(string.Empty, $"{input.Source} could not be opened"),
            };

    private static SourceHash Digested(Stream stream, string source)
    {
        try
        {
            return ArchiveCopy.Sha256Of(stream) is { Length: > 0 } sha ? new SourceHash(sha, string.Empty) : new SourceHash(string.Empty, $"{source} could not be hashed");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new SourceHash(string.Empty, $"{source} could not be read to the end: {e.Message}");
        }
    }

    /// <summary>A folder of this digest is already there: reused when its copy verifies; otherwise — an interrupted stage — removed
    /// whole through the deletion policy and staged fresh (<paramref name="replace"/>), unless its exe is in use, which refuses and
    /// names it. Never written over in place.</summary>
    /// <param name="replace">False for a name another process took between the check and the create: that one is never replaced.</param>
    private static StageReport Existing(StageInput input, string digest, bool replace)
    {
        var exe = ExeOf(input, digest);
        var check = StagedCopies.Check(input.Place.Root, exe, input.OwnVersion, input.Files, input.Archive, input.MaxStageBytes);
        return check.Registrable ? Done(input, StageOutcomes.Reused, digest, check.Version, $"{exe} was already staged and verifies; nothing was written to it")
            : replace ? Replaced(input, digest, check.Reason)
            : Refused(input, digest, $"{FolderOf(input, digest)} was made by another process meanwhile and does not verify ({check.Reason}); it is not replaced");
    }

    /// <summary>The unverified folder removed (exe first: an exe in use keeps it, and the stage is refused naming it), then the copy
    /// made fresh — a retry after an interrupted stage recovers by itself (coai plan round 01e5e611).</summary>
    private static StageReport Replaced(StageInput input, string digest, string unverified)
    {
        var removal = Removed(input.Place, digest, input.Files, $"unverified: {unverified}");
        return removal.Gone
            ? Created(input, digest)
            : Refused(input, digest, $"{FolderOf(input, digest)} already exists and does not verify ({unverified}); it is not replaced: {removal.Folder.Why}");
    }

    /// <summary>The digest folder made (the root first, when missing) and held while the copy is made in it; a copy that failed takes
    /// its own new folder with it again (only when empty: never recursive).</summary>
    private static StageReport Created(StageInput input, string digest)
    {
        var opened = Opened(input, digest);
        if (opened is not FolderBeneath.Ready { Folder: var folder })
        {
            return Refused(input, digest, $"{FolderOf(input, digest)} could not be made: {Why(opened)}");
        }

        CopyResult copied;
        using (folder)
        {
            copied = Copied(input, digest, folder);
        }

        return copied switch
        {
            CopyResult.Done => Done(input, StageOutcomes.Staged, digest, StagedCopies.ReleaseOf(input.OwnVersion), $"{ExeOf(input, digest)} copied, read back equal and recorded in its {StageRecord.FileName}"),
            CopyResult.Taken => Existing(input, digest, replace: false),
            CopyResult.Failed failed => Abandoned(input, digest, failed.Why),
            _ => Refused(input, digest, "the copy could not be made"),
        };
    }

    private static FolderBeneath Opened(StageInput input, string digest)
    {
        try
        {
            input.Files.CreateDirectory(input.Place.Root);
            return input.Archive.OpenFolderBeneath(input.Place.Root, [digest], Scope(input.Place));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new FolderBeneath.Refused(e.Message);
        }
    }

    private static string Why(FolderBeneath opened) => opened is FolderBeneath.Refused refused ? refused.Why : "it could not be opened";

    /// <summary>What making the copy in the held folder came to.</summary>
    private abstract record CopyResult
    {
        public sealed record Done : CopyResult;

        /// <summary>Another process put an exe at the name first — judged as an existing folder, never replaced.</summary>
        public sealed record Taken : CopyResult;

        public sealed record Failed(string Why) : CopyResult;
    }

    private static CopyResult Copied(StageInput input, string digest, BeneathFolder folder) =>
        input.Archive.CreateExclusive(folder, StagedCopies.ExeName, Scope(input.Place)) switch
        {
            ExclusiveFile.Created created => Verified(input, digest, folder, created),
            ExclusiveFile.Exists => new CopyResult.Taken(),
            ExclusiveFile.Refused refused => new CopyResult.Failed($"{ExeOf(input, digest)} could not be created: {refused.Why}"),
            _ => new CopyResult.Failed($"{ExeOf(input, digest)} could not be created"),
        };

    /// <summary>The bytes written and flushed, the folder flushed, the copy read back and compared with the digest, then
    /// <c>stage.json</c> written — the first that fails removes THIS stage's own copy (by its identity), and nothing is recorded; the
    /// answer says whether that removal held (the coai code round on WR-D2, finding 1: never "removed" when it was not).</summary>
    private static CopyResult Verified(StageInput input, string digest, BeneathFolder folder, ExclusiveFile.Created created)
    {
        var problem = CopyProblem(input, digest, folder, created);
        return problem.Length == 0
            ? new CopyResult.Done()
            : new CopyResult.Failed($"{problem}; {Undone(input, digest, input.Archive.RemoveOwnCopy(folder, StagedCopies.ExeName, created.Identity, Scope(input.Place)))}");
    }

    /// <summary>The first step of the copy that failed, in order; empty when every one held.</summary>
    private static string CopyProblem(StageInput input, string digest, BeneathFolder folder, ExclusiveFile.Created created) =>
        Written(input, created) is { Length: > 0 } unwritten ? unwritten
        : FlushProblem(input, digest, folder) is { Length: > 0 } unflushed ? unflushed
        : ReadBackProblem(input, digest, folder) is { Length: > 0 } differs ? differs
        : RecordProblem(input, digest);

    /// <summary>What became of this stage's own copy after a failure — removed, gone, or still there (named, with why).</summary>
    private static string Undone(StageInput input, string digest, VerifiedRemoval removal) => removal switch
    {
        VerifiedRemoval.Removed => $"the copy was removed and no {StageRecord.FileName} written",
        VerifiedRemoval.Gone => $"the copy is gone and no {StageRecord.FileName} written",
        VerifiedRemoval.Kept kept => $"the copy could NOT be removed ({kept.Why}): it stays at {ExeOf(input, digest)} with no {StageRecord.FileName}, and the next stage replaces it",
        VerifiedRemoval.Refused refused => $"the copy could NOT be removed ({refused.Why}): it stays at {ExeOf(input, digest)} with no {StageRecord.FileName}, and the next stage replaces it",
        _ => $"the copy could NOT be removed: it stays at {ExeOf(input, digest)} with no {StageRecord.FileName}",
    };

    /// <summary>Empty when the folder flush held — a name created in a folder that did not flush may not survive a crash, so such a copy
    /// is never recorded (finding 1).</summary>
    private static string FlushProblem(StageInput input, string digest, BeneathFolder folder) => input.Archive.FlushFolder(folder) switch
    {
        FolderFlush.Done => string.Empty,
        FolderFlush.Failed failed => $"{FolderOf(input, digest)} could not be flushed ({failed.Why}); a copy whose name may not survive a crash is not recorded",
        _ => $"{FolderOf(input, digest)} could not be flushed",
    };

    private static string Written(StageInput input, ExclusiveFile.Created created)
    {
        try
        {
            using var target = created.Stream;
            return input.Archive.OpenSource(PathRules.Windows.Parent(input.Source), input.Source) switch
            {
                SourceOpen.Opened opened => Streamed(opened.Stream, target),
                SourceOpen.Refused refused => $"{input.Source} is not copied: {refused.Why}",
                _ => $"{input.Source} went away while it was copied",
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"the copy could not be written: {e.Message}";
        }
    }

    private static string Streamed(Stream source, FileStream target)
    {
        using (source)
        {
            source.CopyTo(target);
            target.Flush(flushToDisk: true);
            return string.Empty;
        }
    }

    /// <summary>Empty when the copy, read back from the disk, hashes to the digest its folder is named for.</summary>
    private static string ReadBackProblem(StageInput input, string digest, BeneathFolder folder)
    {
        var exe = ExeOf(input, digest);
        return input.Archive.ReadBack(folder, StagedCopies.ExeName) switch
        {
            FileHash.Hashed { Sha256: var sha } when sha == digest => string.Empty,
            FileHash.Hashed { Sha256: var sha } => $"{exe} read back as {sha}, not the source's digest {digest}",
            FileHash.Unreadable unreadable => $"{exe} could not be read back: {unreadable.Why}",
            FileHash.Untrusted untrusted => $"{exe} could not be read back from the disk: {untrusted.Why}",
            _ => $"{exe} was not there when read back",
        };
    }

    /// <summary><c>stage.json</c> written atomically (the ledger's writer: a reader sees the old file or the new, never a half);
    /// empty when written.</summary>
    private static string RecordProblem(StageInput input, string digest)
    {
        var record = new StageRecord(StagedCopies.ReleaseOf(input.OwnVersion), digest, input.Now, input.Source);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, WslCareJsonContext.Default.StageRecord);
        try
        {
            return input.Files.WriteFileAtomically(PathRules.Windows.Join(FolderOf(input, digest), StageRecord.FileName), bytes, Scope(input.Place)) is DeletionVerdict.Refused refused
                ? $"{StageRecord.FileName} could not be written: {refused.Reason}"
                : string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"{StageRecord.FileName} could not be written: {e.Message}";
        }
    }

    /// <summary>A copy that failed: its folder (this stage's own, now empty) removed again — never recursively.</summary>
    private static StageReport Abandoned(StageInput input, string digest, string why)
    {
        _ = input.Archive.RemoveEmptyFolder(input.Place.Root, FolderOf(input, digest), Scope(input.Place));
        return Refused(input, digest, why);
    }

    /// <summary>A verified copy in place: the other digest folders swept (the copy just made or reused is kept).</summary>
    private static StageReport Done(StageInput input, string outcome, string digest, string version, string reason)
    {
        var sweep = SweepAfterStage(input.Access, digest);
        return new StageReport(Core.SchemaVersion.Current, outcome, reason, digest, ExeOf(input, digest), version, input.Source, sweep.Swept, sweep.Kept, sweep.Retained);
    }

    private static StageReport Refused(StageInput input, string digest, string reason) =>
        new(Core.SchemaVersion.Current, StageOutcomes.Refused, reason, digest, string.Empty, string.Empty, input.Source, [], [], []);
}
