using WslCare.Core.Agents;
using WslCare.Core.Config;
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
        PlaceKept(c, entry) is { } place ? place
        : Unremovable(entry, indexed) is { } early ? Early(c, entry, early)
        : LiveProblem(c, entry, indexed) is { Length: > 0 } live ? new RemoveOutcome.Kept($"{live}; nothing is touched")
        : CopiesProblem(c, entry, indexed) is { Length: > 0 } damaged ? MarkDamaged(c, entry, indexed, damaged)
        : GitTreeKept(c, entry, indexed, []) is { } git ? git
        : Quarantined(c, entry, indexed);

    /// <summary>A <c>removing</c> entry found by the reconcile: past its commit point, so the quarantined files are removed (the seam
    /// re-hashes each archived copy first) and the entry closed.</summary>
    public static RemoveOutcome Resume(MoveContext c, InflightEntry entry, IndexEntry indexed)
    {
        if (PlaceKept(c, entry) is { } place)
        {
            return place;
        }

        if (Unremovable(entry, indexed) is { } early)
        {
            return Early(c, entry, early);
        }

        IReadOnlyList<Aside> aside = [.. indexed.Files.Select(f => new Aside(f, QuarantinePath(entry.Under, f.Original, entry.QuarantineRun)))];
        if (Liveness.Problem(c.InUse, c.Distro, entry.Agent, entry.Under, entry.Key, ResumeNames(entry, indexed)) is { Length: > 0 } live)
        {
            return Deferred(c, entry, aside, live);
        }

        if (CopiesProblem(c, entry, indexed) is { Length: > 0 } damaged)
        {
            RenameBack(c, entry, aside);
            return MarkDamaged(c, entry, indexed, damaged);
        }

        return GitTreeKept(c, entry, indexed, aside) ?? Finish(c, entry, indexed, aside);
    }

    /// <summary>Security review M-1: phase 2 never acts where the selection would refuse to walk — a link on the way to the agent's
    /// folder (it was stowed into a repository, say), another filesystem, a folder that cannot be resolved. Nothing is touched; the
    /// entry waits.</summary>
    private static RemoveOutcome? PlaceKept(MoveContext c, InflightEntry entry) =>
        AgentWalk.PlaceProblem(c.Stats, c.Home, entry.Under) is { Length: > 0 } why
            ? new RemoveOutcome.Kept($"{entry.Under} is no longer where the selection may walk ({why}); nothing is touched")
            : null;

    /// <summary>Security review M-2: an agent may be working on the unit now — the scan was not complete, a file of it is open, or
    /// Claude Code works in its project. Empty when none can be.</summary>
    private static string LiveProblem(MoveContext c, InflightEntry entry, IndexEntry indexed) =>
        Liveness.Problem(c.InUse, c.Distro, entry.Agent, entry.Under, entry.Key, indexed.Files.Select(f => f.Original));

    /// <summary>The E9.S5 own review, 2: past the commit point the files carry their quarantine names — an agent that held one through the
    /// rename (a handle opened with delete sharing follows the file) holds it under THAT name, and one that wrote anew uses the old one:
    /// both are asked.</summary>
    internal static IReadOnlyList<string> ResumeNames(InflightEntry entry, IndexEntry indexed) =>
        [.. indexed.Files.Select(f => f.Original), .. indexed.Files.Select(f => f.Original + ArchiveNames.QuarantineMark + entry.QuarantineRun)];

    /// <summary>Past the commit point an agent turned out to be working on the unit: every file goes back under its name and the entry
    /// returns to <c>archived</c> — the removal starts again in a later run.</summary>
    private static RemoveOutcome Deferred(MoveContext c, InflightEntry entry, IReadOnlyList<Aside> aside, string why)
    {
        RenameBack(c, entry, aside);
        c.Book.Replace(entry.EntryId, entry with { State = InflightStates.Archived, QuarantineRun = string.Empty });
        return new RemoveOutcome.Kept($"{why}; every file went back under its name and the removal waits");
    }

    /// <summary>Correctness review M5: an entry kept waiting past <c>archive.keptEntryDays</c> since it was archived is let go — dropped
    /// from the in-flight file, its source where it is; the caller returns what is aside. Any other outcome is returned unchanged.</summary>
    public static RemoveOutcome LetGo(MoveContext c, InflightEntry entry, RemoveOutcome outcome)
    {
        var days = Tuning.Current.Int(ConfigKeys.Archive.KeptEntryDays);
        if (outcome is not RemoveOutcome.Kept kept || entry.ArchivedAtUtc == DateTimeOffset.MinValue || c.Clock.GetUtcNow() - entry.ArchivedAtUtc < TimeSpan.FromDays(days))
        {
            return outcome;
        }

        c.Book.Drop(entry.EntryId);
        return new RemoveOutcome.Dropped($"{kept.Why}; it waited past {ConfigKeys.Archive.KeptEntryDays.Name} ({days}) and is let go — its source stays");
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

    /// <summary>Owner rule 2026-10-07: a git working tree that appeared since the copy keeps the WHOLE unit at the source — every file
    /// already aside goes back; found → the entry is superseded (the copies stay a snapshot); not checkable → it waits for the next
    /// run. <c>null</c> when no tree touches it.</summary>
    private static RemoveOutcome? GitTreeKept(MoveContext c, InflightEntry entry, IndexEntry indexed, IReadOnlyList<Aside> aside)
    {
        var found = GitTrees.InUnit(c.Stats, entry.Under, entry.Key, [.. indexed.Files.Select(f => f.Original)]);
        if (found is GitTreeFound.None)
        {
            return null;
        }

        RenameBack(c, entry, aside);
        return found is GitTreeFound.Found tree
            ? Superseded(c, entry, indexed, $"{tree.Entry} is part of a git repository now; nothing of the session is removed and the copies stay a snapshot")
            : new RemoveOutcome.Kept($"whether a git repository touches it is not known ({((GitTreeFound.Unchecked)found).Why}); nothing is removed");
    }

    private static RemoveOutcome Superseded(MoveContext c, InflightEntry entry, IndexEntry indexed, string why)
    {
        _ = ArchiveCopy.AppendLine(c, entry.Agent, entry.Month, Event(c, entry, indexed, ArchiveIndex.Events.Superseded));
        c.Book.Drop(entry.EntryId);
        return new RemoveOutcome.Superseded(why);
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
        var back = Reappeared(c, entry, indexed, kept);
        return Close(c, entry, indexed, kept.Count == 0 && back.Length == 0 ? ArchiveIndex.Events.SourceRemoved : ArchiveIndex.Events.Split, bytes, kept.Count > 0 ? $"{kept.Count} companion file(s) changed after the commit and went back under their names" : back);
    }

    /// <summary>Security review M-2: a file the agent wrote at an original name while the session was aside (it resumed the session by
    /// its path) — the history is in the archive and the new file stays, so the entry is <c>split</c>, never <c>sourceRemoved</c>.</summary>
    private static string Reappeared(MoveContext c, InflightEntry entry, IndexEntry indexed, IReadOnlyList<Aside> kept) =>
        kept.Count == 0 && indexed.Files.FirstOrDefault(f => c.Stats.FileExists(Path.Combine(entry.Under, f.Original))) is { } back
            ? $"{back.Original} exists again (the agent wrote it meanwhile); it stays"
            : string.Empty;

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
