using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;

namespace WslCare.Core.Archive;

/// <summary>What phase 2 did with one entry — a closed set.</summary>
public abstract record RemoveOutcome
{
    private RemoveOutcome()
    {
    }

    /// <summary>The source is gone from the agent's folder (or the agent had removed it); <paramref name="Event"/> is
    /// <c>sourceRemoved</c> or <c>split</c> (a file the agent wrote at an original name meanwhile was kept).</summary>
    public sealed record Removed(long Bytes, int Files, string Event, string Note) : RemoveOutcome;

    /// <summary>Nothing was removed and the entry waits for the next run (a rename refused, the base unreadable).</summary>
    public sealed record Kept(string Why) : RemoveOutcome;

    /// <summary>The source changed after it was archived: every file renamed back, the archived copies stay as a snapshot.</summary>
    public sealed record Superseded(string Why) : RemoveOutcome;

    /// <summary>An archived copy does not match its index line: the source stays, the entry is marked <c>damaged</c>.</summary>
    public sealed record Damaged(string Why) : RemoveOutcome;

    /// <summary>The entry leaves the in-flight file without a removal (restored, or not in the index).</summary>
    public sealed record Dropped(string Why) : RemoveOutcome;
}

/// <summary>
/// Plan §15r D2 phase 2 — in a LATER run, at least <c>archive.removeAfterHours</c> after <c>archived</c>: every archived file opened
/// again and hashed against its index line (a mismatch is <c>damaged</c>, the source stays); every source file renamed in its own
/// folder to <c>&lt;name&gt;.wsl-care-q-&lt;runId&gt;</c>, never replacing; each hashed — any change renames them ALL back (never replacing
/// what the agent wrote since: B1) and the copies stay a snapshot (<c>superseded</c>); all equal → the in-flight entry moves to
/// <c>removing</c>, the COMMIT POINT; then each quarantined file removed by the seam's verified removal, the folders the session left
/// removed when empty, and the <c>sourceRemoved</c> line written.
/// </summary>
public static class ArchiveRemove
{
    /// <summary>A quarantined file: its index row and where it is now.</summary>
    private sealed record Aside(IndexFile File, string Path);

    public static RemoveOutcome Remove(MoveContext c, InflightEntry entry, IndexEntry indexed) =>
        Unremovable(entry, indexed) is { } early ? Early(c, entry, early)
        : CopiesProblem(c, entry, indexed) is { Length: > 0 } damaged ? MarkDamaged(c, entry, indexed, damaged)
        : Quarantined(c, entry, indexed);

    /// <summary>A <c>removing</c> entry found by the reconcile: past its commit point, so the quarantined files are removed (the seam
    /// re-hashes each archived copy first) and the entry closed.</summary>
    public static RemoveOutcome Resume(MoveContext c, InflightEntry entry, IndexEntry indexed)
    {
        if (Unremovable(entry, indexed) is { } early)
        {
            return Early(c, entry, early);
        }

        IReadOnlyList<Aside> aside = [.. indexed.Files.Select(f => new Aside(f, QuarantinePath(entry.Under, f.Original, entry.QuarantineRun)))];
        if (CopiesProblem(c, entry, indexed) is { Length: > 0 } damaged)
        {
            RenameBack(c, entry, aside);
            return MarkDamaged(c, entry, indexed, damaged);
        }

        return Finish(c, entry, indexed, aside);
    }

    private static RemoveOutcome? Unremovable(InflightEntry entry, IndexEntry indexed) =>
        indexed.Status == ArchiveIndex.Events.Restored ? new RemoveOutcome.Dropped("it was restored; a restored session is never removed")
        : !indexed.Verified ? new RemoveOutcome.Kept("its index lines do not all carry this side's MAC; nothing is removed on an unverified line")
        : indexed.Files.Count == 0 ? new RemoveOutcome.Dropped("its index holds no files for it")
        : !string.Equals(indexed.Agent, entry.Agent, StringComparison.Ordinal) ? new RemoveOutcome.Kept("its index line names another agent")
        : null;

    private static RemoveOutcome Early(MoveContext c, InflightEntry entry, RemoveOutcome early)
    {
        if (early is RemoveOutcome.Dropped)
        {
            c.Book.Drop(entry.EntryId);
        }

        return early;
    }

    /// <summary>Every archived file opened again and hashed against its index row; empty when all equal (D2 step 7).</summary>
    private static string CopiesProblem(MoveContext c, InflightEntry entry, IndexEntry indexed) =>
        indexed.Files.Select(f => CopyProblem(c, entry, f)).FirstOrDefault(p => p.Length > 0) ?? string.Empty;

    private static string CopyProblem(MoveContext c, InflightEntry entry, IndexFile file)
    {
        var levels = ArchiveCopy.Levels(entry.Agent, entry.Month, c.Side, ArchiveCopy.Folders(file.Archived));
        if (c.Files.OpenExistingFolderBeneath(c.BaseFolder, levels) is not FolderBeneath.Ready { Folder: var folder })
        {
            return $"the folder of {file.Archived} is missing or unreadable in the base";
        }

        using (folder)
        {
            return c.Files.ReadBack(folder, Path.GetFileName(file.Archived)) is FileHash.Hashed hashed && hashed.Sha256 == file.Sha256 && hashed.Length == file.Bytes
                ? string.Empty
                : $"{file.Archived} no longer matches its index line";
        }
    }

    private static RemoveOutcome MarkDamaged(MoveContext c, InflightEntry entry, IndexEntry indexed, string why)
    {
        _ = ArchiveCopy.AppendLine(c, entry.Agent, entry.Month, Event(c, entry, indexed, ArchiveIndex.Events.Damaged));
        c.Book.Drop(entry.EntryId);
        return new RemoveOutcome.Damaged($"{why}; the source stays and the entry is marked damaged");
    }

    private static RemoveOutcome Quarantined(MoveContext c, InflightEntry entry, IndexEntry indexed)
    {
        c.Step(MoveSteps.QuarantineStart);
        var aside = new List<Aside>();
        foreach (var file in indexed.Files)
        {
            var renamed = Rename(c, entry, file);
            if (renamed is RenameStop stop)
            {
                RenameBack(c, entry, aside);
                return new RemoveOutcome.Kept(stop.Why);
            }

            if (renamed is RenameDone done)
            {
                aside.Add(done.Aside);
            }
        }

        return aside.Count == 0 ? Close(c, entry, indexed, ArchiveIndex.Events.SourceRemoved, 0, "the agent had removed every file of it") : Checked(c, entry, indexed, aside);
    }

    private abstract record RenameResult;

    private sealed record RenameDone(Aside Aside) : RenameResult;

    private sealed record RenameGone : RenameResult;

    private sealed record RenameStop(string Why) : RenameResult;

    private static RenameResult Rename(MoveContext c, InflightEntry entry, IndexFile file)
    {
        var original = Path.Combine(entry.Under, file.Original);
        var quarantined = QuarantinePath(entry.Under, file.Original, c.RunId);
        return c.Files.QuarantineRename(entry.Under, original, Path.GetFileName(quarantined), QuarantineScope(entry)) switch
        {
            NoReplaceRename.Renamed => new RenameDone(new Aside(file, quarantined)),
            NoReplaceRename.Gone => new RenameGone(),
            NoReplaceRename.NameTaken => new RenameStop($"{file.Original}: its quarantine name is taken; nothing was removed"),
            NoReplaceRename.Refused refused => new RenameStop($"{file.Original} could not be renamed aside ({refused.Why}); nothing was removed"),
            _ => new RenameStop(file.Original),
        };
    }

    /// <summary>Each quarantined file hashed through the seam's no-link opener: all equal → the commit point; any changed → back.</summary>
    private static RemoveOutcome Checked(MoveContext c, InflightEntry entry, IndexEntry indexed, IReadOnlyList<Aside> aside)
    {
        if (aside.FirstOrDefault(a => !Unchanged(c, entry, a)) is { } changed)
        {
            var split = RenameBack(c, entry, aside);
            _ = ArchiveCopy.AppendLine(c, entry.Agent, entry.Month, Event(c, entry, indexed, split ? ArchiveIndex.Events.Split : ArchiveIndex.Events.Superseded));
            c.Book.Drop(entry.EntryId);
            return new RemoveOutcome.Superseded($"{changed.File.Original} changed after it was archived; every file was renamed back and the copies stay as a snapshot");
        }

        var committed = entry with { State = InflightStates.Removing, QuarantineRun = c.RunId };
        if (c.Book.Replace(entry.EntryId, committed) is { Length: > 0 } unwritten)
        {
            RenameBack(c, entry, aside);
            return new RemoveOutcome.Kept($"the commit point could not be written ({unwritten}); every file was renamed back");
        }

        return Finish(c, committed, indexed, aside);
    }

    private static bool Unchanged(MoveContext c, InflightEntry entry, Aside aside) => c.Files.OpenSource(entry.Under, aside.Path) switch
    {
        SourceOpen.Opened opened => ArchiveCopy.Sha256Of(opened.Stream) == aside.File.Sha256,
        _ => false,
    };

    /// <summary>Every quarantined file renamed back, never replacing (B1): when the original name exists again — the agent made it
    /// meanwhile — the agent's file is kept and ours is removed only when it equals its archived copy. True when that happened.</summary>
    private static bool RenameBack(MoveContext c, InflightEntry entry, IEnumerable<Aside> aside)
    {
        var split = false;
        foreach (var file in aside)
        {
            var back = c.Files.RenameBack(entry.Under, file.Path, Path.GetFileName(file.File.Original), QuarantineScope(entry));
            if (back is NoReplaceRename.NameTaken)
            {
                split = true;
                _ = c.Files.RemoveVerified(entry.Under, file.Path, file.File.Sha256, ArchivedCopy(c, entry, file.File), RemovalScope(entry));
            }
        }

        return split;
    }

    /// <summary>After the commit point: each quarantined file removed by the seam (the archived copy re-hashed, the quarantined bytes
    /// hashed, Linux under a write lease, Windows through one handle).</summary>
    /// <remarks>Consult 26b4a958 C-3 — removal is per UNIT: the session's own file (its key, the transcript) is removed FIRST. When it
    /// changed after the commit point (the seam keeps it), not one file of the unit is removed: every quarantined file goes back under
    /// its name and the copies stay a snapshot (<c>superseded</c>) — a transcript is never left at the source without its companions.
    /// Once the transcript is gone, a companion that changed after the commit goes back under its own name (it holds what the agent
    /// wrote since its copy) and the entry is <c>split</c>.</remarks>
    private static RemoveOutcome Finish(MoveContext c, InflightEntry entry, IndexEntry indexed, IReadOnlyList<Aside> aside)
    {
        var ordered = aside.OrderBy(a => a.File.Original == entry.Key ? 0 : 1).ToList();
        long bytes = 0;
        var kept = new List<Aside>();
        foreach (var file in ordered)
        {
            var removed = c.Files.RemoveVerified(entry.Under, file.Path, file.File.Sha256, ArchivedCopy(c, entry, file.File), RemovalScope(entry));
            var stays = removed is VerifiedRemoval.Kept or VerifiedRemoval.Refused;
            if (stays && file.File.Original == entry.Key)
            {
                return TranscriptChanged(c, entry, indexed, ordered);
            }

            bytes += removed is VerifiedRemoval.Removed ? file.File.Bytes : 0;
            kept.AddRange(stays ? [file] : []);
            c.Progress(removed is VerifiedRemoval.Removed ? file.File.Bytes : 0, true);
        }

        _ = RenameBack(c, entry, kept);
        c.Step(MoveSteps.FoldersStart);
        RemoveFolders(c, entry, indexed);
        return Close(c, entry, indexed, kept.Count == 0 ? ArchiveIndex.Events.SourceRemoved : ArchiveIndex.Events.Split, bytes, kept.Count == 0 ? string.Empty : $"{kept.Count} companion file(s) changed after the commit and went back under their names");
    }

    /// <summary>The transcript changed after the commit point: the whole unit goes back, nothing of it is removed (C-3).</summary>
    private static RemoveOutcome TranscriptChanged(MoveContext c, InflightEntry entry, IndexEntry indexed, IReadOnlyList<Aside> aside)
    {
        var split = RenameBack(c, entry, aside);
        _ = ArchiveCopy.AppendLine(c, entry.Agent, entry.Month, Event(c, entry, indexed, split ? ArchiveIndex.Events.Split : ArchiveIndex.Events.Superseded));
        c.Book.Drop(entry.EntryId);
        return new RemoveOutcome.Superseded($"{entry.Key} changed after the commit point; no file of the session was removed and every one went back under its name");
    }

    /// <summary>The folders the session's files left, deepest first, removed when EMPTY — never the layout's top-level folders, never
    /// the folder of the session's own key (a project folder holds other sessions), never recursive.</summary>
    private static void RemoveFolders(MoveContext c, InflightEntry entry, IndexEntry indexed)
    {
        foreach (var folder in LeftFolders(entry.Key, indexed.Files.Select(f => f.Original)))
        {
            _ = c.Files.RemoveEmptyFolder(entry.Under, Path.Combine(entry.Under, folder), RemovalScope(entry));
        }
    }

    /// <summary>The candidate folders: every parent of a file that is neither the key's folder nor one of its ancestors, and lies at
    /// least two levels deep; deepest first.</summary>
    public static IReadOnlyList<string> LeftFolders(string key, IEnumerable<string> originals)
    {
        var keyFolder = string.Join('/', ArchiveCopy.Folders(key));
        return [.. originals.SelectMany(Parents).Distinct(StringComparer.Ordinal)
            .Where(f => f.Contains('/', StringComparison.Ordinal) && !IsAncestorOrSelf(f, keyFolder))
            .OrderByDescending(f => f.Count(ch => ch == '/')).ThenBy(f => f, StringComparer.Ordinal)];
    }

    private static IEnumerable<string> Parents(string relative)
    {
        var folders = ArchiveCopy.Folders(relative);
        return Enumerable.Range(1, folders.Count).Select(n => string.Join('/', folders.Take(n)));
    }

    private static bool IsAncestorOrSelf(string folder, string of) =>
        of == folder || of.StartsWith(folder + "/", StringComparison.Ordinal);

    private static RemoveOutcome Close(MoveContext c, InflightEntry entry, IndexEntry indexed, string ending, long bytes, string note)
    {
        c.Step(MoveSteps.CloseStart);
        if (ArchiveCopy.AppendLine(c, entry.Agent, entry.Month, Event(c, entry, indexed, ending)) is { Length: > 0 } unwritten)
        {
            return new RemoveOutcome.Kept($"the source was removed but its {ending} line could not be written ({unwritten}); the entry stays for the next run");
        }

        c.Step(MoveSteps.Closed);
        c.Book.Drop(entry.EntryId);
        return new RemoveOutcome.Removed(bytes, indexed.Files.Count, ending, note);
    }

    /// <summary>A status event for the entry (no files: readers merge it with the <c>archived</c> event).</summary>
    private static IndexLine Event(MoveContext c, InflightEntry entry, IndexEntry indexed, string name) =>
        new(ArchiveIndex.SchemaVersion, name, indexed.EntryId, entry.Agent, c.Side, entry.Key, entry.Month, c.Clock.GetUtcNow(), c.Zone.Id, c.RunId, [], string.Empty);

    public static string QuarantinePath(string under, string original, string runId) =>
        Path.Combine(under, original) + ArchiveNames.QuarantineMark + runId;

    private static string ArchivedCopy(MoveContext c, InflightEntry entry, IndexFile file) =>
        Path.Combine([c.BaseFolder, .. ArchiveCopy.Levels(entry.Agent, entry.Month, c.Side, []), .. file.Archived.Split('/')]);

    private static DeletionScope QuarantineScope(InflightEntry entry) => new(entry.Under, "A13", DeletionPermit.ArchiveQuarantine);

    private static DeletionScope RemovalScope(InflightEntry entry) => new(entry.Under, "A13", DeletionPermit.ArchiveRemoval);
}
