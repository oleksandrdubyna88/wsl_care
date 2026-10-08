using System.Security.Cryptography;

using WslCare.Core.Agents;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;

namespace WslCare.Core.Archive;

/// <summary>What <c>archive restore</c> was asked for (plan §15r D6): entry ids, or one agent's month (<c>yyyy-MM</c>), or one session
/// of one agent by its path relative to the agent's layout root; and whether an UNVERIFIED entry may be restored.</summary>
public sealed record RestoreRequest(IReadOnlyList<string> EntryIds, string Agent, string Month, string Session, bool AcceptUnverified)
{
    public static RestoreRequest None { get; } = new([], string.Empty, string.Empty, string.Empty, false);

    /// <summary>Whether this is a restore at all (a run or a scan carries <see cref="None"/>).</summary>
    public bool Asked => EntryIds.Count > 0 || Month.Length > 0 || Session.Length > 0;
}

/// <summary>Where a restored session stands — a closed set.</summary>
public static class RestoreOutcomes
{
    public const string Restored = "restored";

    /// <summary>Every file of it is already in the agent folder with the archived bytes.</summary>
    public const string AlreadyThere = "already-there";

    public const string Refused = "refused";

    /// <summary>A split entry: the missing files restored, the ones the agent changed kept as they are (E9.S3 own review round C-4).</summary>
    public const string Partial = "partial";

    /// <summary>An entry id or a session asked for that no readable index of this side holds (C-3).</summary>
    public const string NotFound = "not-found";

    /// <summary>A month index that could not be read: what it holds is not known (C-3).</summary>
    public const string Unreadable = "unreadable";

    /// <summary>Not started: the restore's budget (archive.restoreLimitMinutes) ran out before it (the gate round).</summary>
    public const string Stopped = "stopped";
}

/// <summary>One session a restore looked at: what became of it and why.</summary>
public sealed record RestoredSession(string EntryId, string Agent, string Key, string Month, string Outcome, int Files, long Bytes, string Note);

/// <summary>What <c>archive restore</c> did.</summary>
public sealed record RestoreReport(int Restored, int AlreadyThere, int Refused, IReadOnlyList<RestoredSession> Sessions)
{
    public static RestoreReport Empty { get; } = new(0, 0, 0, []);

    /// <summary>The session counted: a partial restore counts as restored (files were created); not found and unreadable count as
    /// refused — the restore did not do what it was asked (E9.S3 own review round C-3).</summary>
    public RestoreReport With(RestoredSession session) => this with
    {
        Restored = Restored + (session.Outcome is RestoreOutcomes.Restored or RestoreOutcomes.Partial ? 1 : 0),
        AlreadyThere = AlreadyThere + (session.Outcome == RestoreOutcomes.AlreadyThere ? 1 : 0),
        Refused = Refused + (session.Outcome is RestoreOutcomes.Refused or RestoreOutcomes.NotFound or RestoreOutcomes.Unreadable or RestoreOutcomes.Stopped ? 1 : 0),
        Sessions = [.. Sessions, session],
    };
}

/// <summary>
/// Plan §15r D6 — <c>archive restore</c>, as the user, under the side's lock and lease. Per session, decided whole before any write:
/// its index entry is VERIFIED (or <c>--accept-unverified</c> was given; a <c>recovered</c> one is never verified); it is not on its
/// way (in the in-flight file); every file's target is the CURRENT layout root joined with its original RELATIVE path, inside the
/// agent's session layout (the unit's glob, or a companion of it) and never what never moves; every archived copy, inside the
/// entry's own <c>&lt;agent&gt;/&lt;yyyy&gt;/&lt;MM&gt;/&lt;side&gt;/</c> folder, hashes equal to its index row; a target that exists with the
/// same bytes is already restored, with other bytes refuses the WHOLE session. Then each missing file is CREATED (never replacing,
/// through no link — the seam's exclusive create under the <see cref="DeletionPermit.RestoreIntoAgentFolder"/> permit, which
/// allows nothing else), streamed from its archived copy, flushed, given the RESTORE time as its last write (Claude's own sweep
/// must not delete it at its next start — D6, Q3) and read back equal. The archived copies STAY; a <c>restored</c> event is
/// appended and the entry joins <c>restored.json</c>.
/// </summary>
public static class ArchiveRestore
{
    /// <summary>One entry asked for, with the agent it belongs to and that agent's layout root on this side; <paramref name="Chosen"/>
    /// says why this one was taken when several matched.</summary>
    public sealed record Candidate(ArchiveTarget Target, IndexEntry Entry)
    {
        public string Chosen { get; init; } = string.Empty;
    }

    /// <summary>What a request names, decided before anything is touched: the entries to restore, and the rows that answer what could
    /// not even be looked at (not found, an index that cannot be read, an id found twice).</summary>
    public sealed record RestoreSelection(IReadOnlyList<Candidate> Candidates, IReadOnlyList<RestoredSession> Answered);

    public static RestoreReport Restore(MoveContext c, IReadOnlyList<Candidate> candidates, bool acceptUnverified) =>
        Restore(c, new RestoreSelection(candidates, []), acceptUnverified, static _ => false);

    /// <summary>Every row answered, then each candidate restored — until <paramref name="wouldOverrun"/> says the next session (its
    /// declared bytes) does not fit the budget (<c>archive.restoreLimitMinutes</c>, the coai code round): the rest are <c>stopped</c>.</summary>
    public static RestoreReport Restore(MoveContext c, RestoreSelection selection, bool acceptUnverified, Func<long, bool> wouldOverrun) =>
        selection.Candidates.Aggregate(
            selection.Answered.Aggregate(RestoreReport.Empty, (report, row) => report.With(row)),
            (report, candidate) => report.With(wouldOverrun(candidate.Entry.Files.Sum(f => f.Bytes))
                ? Session(candidate.Entry, RestoreOutcomes.Stopped, 0, "the restore's budget (archive.restoreLimitMinutes) ran out before it; restore it again")
                : One(c, candidate, acceptUnverified)));

    /// <summary>The statuses of an entry whose source is gone — the ones a session or a month restore takes.</summary>
    private static bool SourceGone(IndexEntry entry) => entry.Status is ArchiveIndex.Events.SourceRemoved or ArchiveIndex.Events.Split;

    /// <summary>E9.S3 own review round S-M1, C-2, C-3: the entries of this side's readable month indexes whose agent and month are the
    /// folder's own (a line naming another is never taken); every unreadable month answered; then by id (one entry per id — an id found
    /// in two months restores nothing and names them), by session (its NEWEST entry whose source is gone, verified first), or by month
    /// (each session of the month once, the same way).</summary>
    public static RestoreSelection Select(MoveContext c, IFileSystem files, RestoreRequest asked, IReadOnlyList<ArchiveTarget> targets)
    {
        var months = ArchiveList.Months(c, files, targets, ArchiveList.MonthOf(asked.Month)).ToList();
        IReadOnlyList<RestoredSession> unread = [.. months.Where(m => m.Read is MonthRead.Unreadable).Select(m => Row(string.Empty, m.Target.Entry.Id, string.Empty, m.Month, RestoreOutcomes.Unreadable, $"its index could not be read ({((MonthRead.Unreadable)m.Read).Why}); what it holds is not known"))];
        var all = months.Where(m => m.Read is MonthRead.Read)
            .SelectMany(m => ArchiveIndex.Merge(((MonthRead.Read)m.Read).Index.Records).Where(e => e.Agent == m.Target.Entry.Id && e.Month == m.Month).Select(e => new Candidate(m.Target, e)))
            .ToList();
        var picked = asked.EntryIds.Count > 0 ? ById(all, asked.EntryIds)
            : asked.Session.Length > 0 ? BySession(all, asked.Session)
            : new RestoreSelection([.. all.Where(x => SourceGone(x.Entry)).GroupBy(x => x.Target.Entry.Id + "\n" + x.Entry.Key, StringComparer.Ordinal).Select(g => Newest([.. g]))], []);
        return picked with { Answered = [.. unread, .. picked.Answered] };
    }

    private static RestoreSelection ById(IReadOnlyList<Candidate> all, IReadOnlyList<string> ids)
    {
        var candidates = new List<Candidate>();
        var rows = new List<RestoredSession>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            var matches = all.Where(x => x.Entry.EntryId == id).ToList();
            if (matches.Count == 1)
            {
                candidates.Add(matches[0]);
                continue;
            }

            rows.Add(matches.Count == 0
                ? Row(id, string.Empty, string.Empty, string.Empty, RestoreOutcomes.NotFound, "no readable index of this side holds that entry id")
                : Row(id, matches[0].Entry.Agent, matches[0].Entry.Key, string.Empty, RestoreOutcomes.Refused, $"the id is in {matches.Count} months ({string.Join(", ", matches.Select(x => x.Entry.Month).Order(StringComparer.Ordinal))}) — one may be planted on the share; nothing is restored"));
        }

        return new RestoreSelection(candidates, rows);
    }

    private static RestoreSelection BySession(IReadOnlyList<Candidate> all, string session)
    {
        var gone = all.Where(x => x.Entry.Key == session && SourceGone(x.Entry)).ToList();
        return gone.Count > 0
            ? new RestoreSelection([Newest(gone)], [])
            : new RestoreSelection([], [Row(string.Empty, string.Empty, session, string.Empty, RestoreOutcomes.NotFound, "no entry of that session whose source was removed is in a readable index of this side")]);
    }

    /// <summary>C-2, S-M1: the newest of a session's entries, a verified one before any other; it says so when there was a choice.</summary>
    private static Candidate Newest(IReadOnlyList<Candidate> matches)
    {
        var newest = matches.OrderByDescending(x => x.Entry.Verified).ThenByDescending(x => x.Entry.ArchivedAtUtc).First();
        return matches.Count == 1 ? newest : newest with { Chosen = $"the newest verified of {matches.Count} entries of the session ({newest.Entry.EntryId}, {newest.Entry.Month}); " };
    }

    private static RestoredSession Row(string entryId, string agent, string key, string month, string outcome, string note) =>
        new(entryId, agent, key, month, outcome, 0, 0, note);

    private static RestoredSession One(MoveContext c, Candidate candidate, bool acceptUnverified)
    {
        var (target, entry) = (candidate.Target, candidate.Entry);
        if (Unrestorable(c, target, entry, acceptUnverified) is { Length: > 0 } why)
        {
            return Session(entry, RestoreOutcomes.Refused, 0, why);
        }

        var plan = Planned(c, target.Under, entry);
        var session = plan.Problem.Length > 0 ? Session(entry, RestoreOutcomes.Refused, 0, plan.Problem)
            : plan.Missing.Count == 0 ? Session(entry, RestoreOutcomes.AlreadyThere, 0, "every file is already there with its archived bytes")
            : SpaceProblem(c, target.Under, plan.Missing) is { Length: > 0 } space ? Session(entry, RestoreOutcomes.Refused, 0, space)
            : Written(c, target, entry, plan);
        return session with { Note = candidate.Chosen + session.Note };
    }

    /// <summary>S-M1: the agent folder's disk must hold what the index says will be written — a hostile index naming huge files never
    /// fills it; a disk that cannot be measured is not refused (the stream is capped at each file's indexed length anyway).</summary>
    private static string SpaceProblem(MoveContext c, string under, IReadOnlyList<IndexFile> missing)
    {
        var needed = missing.Sum(f => f.Bytes);
        return c.Stats.MeasureVolume(under) is VolumeReadResult.Measured volume && volume.AvailableBytes < needed
            ? $"the agent folder's disk has {volume.AvailableBytes} bytes free, the session needs {needed}; nothing is restored"
            : string.Empty;
    }

    private static RestoredSession Session(IndexEntry entry, string outcome, long bytes, string note) =>
        new(entry.EntryId, entry.Agent, entry.Key, entry.Month, outcome, entry.Files.Count, bytes, note);

    /// <summary>Why the entry is never restored as it stands; empty when it may be.</summary>
    private static string Unrestorable(MoveContext c, ArchiveTarget target, IndexEntry entry, bool acceptUnverified) =>
        target.Under.Length == 0 ? $"{entry.Agent} keeps no session layout on this side ({target.Refusal})"
        : entry.Files.Count == 0 ? "its index holds no files for it"
        : !entry.Verified && !acceptUnverified ? "its index lines are not all this side's (unverified, or recovered by a scan); it is restored only with --accept-unverified"
        : c.Book.Entries.Any(e => e.EntryId == entry.EntryId || (e.Agent == entry.Agent && e.Key == entry.Key)) ? "it is on its way through an archive run (in the in-flight file); restore it after the run finished it"
        : LayoutProblem(target.Entry, entry) is { Length: > 0 } layout ? layout
        : PlaceProblem(c, target, entry);

    /// <summary>E9.S3 own review round S-B1 (owner rule 2026-10-07): nothing is ever restored where the selection would not walk — a
    /// link on the way to the agent's folder, another filesystem — nor inside a git working tree: a <c>.git</c> above the key's folder
    /// (spelled or real path), on the way to a file, below a companion folder, or named by a file itself.</summary>
    private static string PlaceProblem(MoveContext c, ArchiveTarget target, IndexEntry entry) =>
        AgentWalk.PlaceProblem(c.Stats, c.Home, target.Under) is { Length: > 0 } place ? $"{target.Under} is not where the selection may walk ({place}); nothing is restored"
        : GitTrees.InUnit(c.Stats, target.Under, entry.Key, [.. entry.Files.Select(f => f.Original)]) switch
        {
            GitTreeFound.Found found => $"{found.Entry} is part of a git repository; nothing is restored inside a working tree",
            GitTreeFound.Unchecked notChecked => $"whether a git repository is on its way is not known ({notChecked.Why}); nothing is restored",
            _ => string.Empty,
        };

    /// <summary>Review M4: every file must be the unit's own — the key a session (or file unit) of the agent's layout, every other file
    /// inside a companion of it — and none may name what never moves.</summary>
    internal static string LayoutProblem(AgentEntry agent, IndexEntry entry)
    {
        var unit = agent.Archive!.Units.FirstOrDefault(u => GlobMatches(UnitGlob(agent, u), entry.Key));
        if (unit is null)
        {
            return $"{entry.Key} is not a session of {agent.Name}'s layout";
        }

        var companions = unit.Kind == ArchiveUnitKinds.Session ? agent.Sessions!.Companions : [];
        return Selection.CompanionProblem(entry.Key, companions) is { Length: > 0 } bad ? bad
            : entry.Files.FirstOrDefault(f => !InUnit(f.Original, entry.Key, companions)) is { } stray ? $"{stray.Original} is neither the session nor inside one of its companions; nothing of it is restored"
            : entry.Files.FirstOrDefault(f => AgentArchiveRules.IsNeverMoved(agent.Archive!, f.Original)) is { } never ? $"{never.Original} names what never moves; nothing of it is restored"
            : string.Empty;
    }

    private static string UnitGlob(AgentEntry agent, ArchiveUnit unit) => unit.Kind == ArchiveUnitKinds.Session ? agent.Sessions!.Glob : unit.Glob;

    private static bool InUnit(string original, string key, IReadOnlyList<string> companions) =>
        original == key || companions.Select(t => Selection.Expand(t, key)).Any(c => original == c || original.StartsWith(c + "/", StringComparison.Ordinal));

    /// <summary>Whether a relative path matches a layout glob, one pattern per segment, <c>**</c> any number of folders.</summary>
    internal static bool GlobMatches(string glob, string relative) => Matches(glob.Split('/'), 0, relative.Split('/'), 0);

    private static bool Matches(string[] glob, int g, string[] path, int p) =>
        g == glob.Length ? p == path.Length
        : glob[g] == SessionGlob.AnyDepth ? Matches(glob, g + 1, path, p) || (p < path.Length && Matches(glob, g, path, p + 1))
        : p < path.Length && SessionGlob.Matches(glob[g], path[p]) && Matches(glob, g + 1, path, p + 1);

    /// <summary>The files still to create, the files a SPLIT entry keeps as the agent changed them, or why the session is refused —
    /// decided for EVERY file before any is written.</summary>
    private sealed record RestorePlan(IReadOnlyList<IndexFile> Missing, IReadOnlyList<IndexFile> Kept, string Problem);

    private static RestorePlan Planned(MoveContext c, string under, IndexEntry entry)
    {
        var missing = new List<IndexFile>();
        var kept = new List<IndexFile>();
        foreach (var file in entry.Files)
        {
            var problem = CopyProblem(c, entry, file) is { Length: > 0 } damaged ? damaged : TargetProblem(c, under, file, missing);
            if (problem.Length > 0 && entry.Status == ArchiveIndex.Events.Split && problem.StartsWith(OtherBytes, StringComparison.Ordinal))
            {
                kept.Add(file);
                continue;
            }

            if (problem.Length > 0)
            {
                return new RestorePlan([], [], $"{file.Original} {problem}");
            }
        }

        return new RestorePlan(missing, kept, string.Empty);
    }

    /// <summary>The start of the answer for a target that exists with other bytes — C-4: a split entry keeps such a file.</summary>
    private const string OtherBytes = "exists with other bytes";

    /// <summary>D6: the archived copy, inside the entry's own folder, opened and hashed first — a damaged copy is never restored.</summary>
    private static string CopyProblem(MoveContext c, IndexEntry entry, IndexFile file)
    {
        var levels = ArchiveCopy.Levels(entry.Agent, entry.Month, c.Side, ArchiveCopy.Folders(file.Archived));
        if (c.Files.OpenExistingFolderBeneath(c.BaseFolder, levels) is not FolderBeneath.Ready { Folder: var folder })
        {
            return $"the folder of {file.Archived} is missing or unreadable in the base; nothing is restored";
        }

        using (folder)
        {
            return c.Files.ReadBack(folder, Path.GetFileName(file.Archived)) is FileHash.Hashed hashed && hashed.Sha256 == file.Sha256 && hashed.Length == file.Bytes
                ? string.Empty
                : $"{file.Archived} no longer matches its index line (damaged); nothing is restored";
        }
    }

    /// <summary>Never overwrite: a target with the archived bytes is already restored; with other bytes the session is refused.</summary>
    private static string TargetProblem(MoveContext c, string under, IndexFile file, List<IndexFile> missing)
    {
        switch (c.Files.OpenSource(under, Path.Combine(under, file.Original)))
        {
            case SourceOpen.Gone:
                missing.Add(file);
                return string.Empty;
            case SourceOpen.Opened opened:
                return ArchiveCopy.Sha256Of(opened.Stream) == file.Sha256 ? string.Empty : $"{OtherBytes} (a live session of that name); nothing of it is restored";
            case SourceOpen.Refused refused:
                return $"cannot be checked ({refused.Why}); nothing of it is restored";
            default:
                return "cannot be checked";
        }
    }

    /// <summary>Every missing file created; then the <c>restored</c> event and <c>restored.json</c>.</summary>
    private static RestoredSession Written(MoveContext c, ArchiveTarget target, IndexEntry entry, RestorePlan plan)
    {
        var missing = plan.Missing;
        long bytes = 0;
        foreach (var file in missing)
        {
            if (Created(c, target.Under, entry, file) is { Length: > 0 } failed)
            {
                return Session(entry, RestoreOutcomes.Refused, bytes, $"{failed}; the files restored before it stay (each was verified and promoted whole), nothing of this one is left");
            }

            bytes += file.Bytes;
            c.Progress(file.Bytes, true);
        }

        var restored = new IndexLine(ArchiveIndex.SchemaVersion, ArchiveIndex.Events.Restored, entry.EntryId, entry.Agent, c.Side, entry.Key, entry.Month, c.Clock.GetUtcNow(), c.Zone.Id, c.RunId, [], string.Empty);
        var unwritten = ArchiveCopy.AppendLine(c, entry.Agent, entry.Month, restored) is { Length: > 0 } index ? $"; its restored line could not be written ({index})" : string.Empty;
        var unrecorded = c.Book.AddRestored(new RestoredEntry(entry.EntryId, entry.Agent, entry.Key, entry.Month) { RestoredAtUtc = c.Clock.GetUtcNow() }, c.Clock.GetUtcNow()) is { Length: > 0 } local ? $"; restored.json could not be written ({local})" : string.Empty;
        return plan.Kept.Count == 0
            ? Session(entry, RestoreOutcomes.Restored, bytes, $"{missing.Count} file(s) created{unwritten}{unrecorded}")
            : Session(entry, RestoreOutcomes.Partial, bytes, $"{missing.Count} file(s) created; kept as the agent changed them: {string.Join(", ", plan.Kept.Select(f => f.Original))}{unwritten}{unrecorded}");
    }

    private static DeletionScope RestoreScope(string under) => new(under, "A20", DeletionPermit.RestoreIntoAgentFolder);

    /// <summary>One file created under its original name (never replacing, through no link), streamed from its archived copy,
    /// flushed, its last write the restore time, read back equal. Empty when restored.</summary>
    private static string Created(MoveContext c, string under, IndexEntry entry, IndexFile file)
    {
        var scope = RestoreScope(under);
        if (c.Files.OpenFolderBeneath(under, ArchiveCopy.Folders(file.Original), scope) is not FolderBeneath.Ready { Folder: var folder })
        {
            return $"the folder of {file.Original} could not be made in {under}";
        }

        using (folder)
        {
            var temporary = Path.GetFileName(file.Original) + ArchiveNames.RestoreMark + c.RunId;
            return c.Files.CreateExclusive(folder, temporary, scope) switch
            {
                ExclusiveFile.Created created => Filled(c, under, entry, file, folder, temporary, created),
                ExclusiveFile.Exists => $"{file.Original}: a temporary copy of an earlier restore ({temporary}) is in the way; it is left for a person",
                ExclusiveFile.Refused refused => $"{file.Original} could not be created ({refused.Why})",
                _ => $"{file.Original} could not be created",
            };
        }
    }

    /// <summary>S-B2 / C-1: the copy streamed into the TEMPORARY name, capped at the indexed length, hashed, flushed, read back — then
    /// promoted to the session's name without replacing. Any failure removes that temporary file (by the identity the create gave it),
    /// so nothing that did not match ever stands under the session's name, and a later restore is not refused for ever.</summary>
    private static string Filled(MoveContext c, string under, IndexEntry entry, IndexFile file, BeneathFolder folder, string temporary, ExclusiveFile.Created created)
    {
        var problem = Verified(c, entry, file, folder, temporary, created) is { Length: > 0 } unverified ? unverified : Promoted(c, under, file, folder, temporary);
        if (problem.Length > 0)
        {
            _ = c.Files.RemoveOwnCopy(folder, temporary, created.Identity, RestoreScope(under));
        }

        return problem;
    }

    private static string Verified(MoveContext c, IndexEntry entry, IndexFile file, BeneathFolder folder, string temporary, ExclusiveFile.Created created)
    {
        var sideFolder = Path.Combine([c.BaseFolder, .. ArchiveCopy.Levels(entry.Agent, entry.Month, c.Side, [])]);
        try
        {
            if (Streamed(c, sideFolder, file, created) is { Length: > 0 } unread)
            {
                return unread;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"{file.Original} could not be written ({e.Message})";
        }

        return c.Files.FlushFolder(folder) is FolderFlush.Done && c.Files.ReadBack(folder, temporary) is FileHash.Hashed back && back.Sha256 == file.Sha256 && back.Length == file.Bytes
            ? string.Empty
            : $"{file.Original} did not read back equal to its archived copy";
    }

    private static string Promoted(MoveContext c, string under, IndexFile file, BeneathFolder folder, string temporary) =>
        c.Files.PromoteRestored(under, Path.Combine(under, ArchiveCopy.FolderOf(file.Original), temporary), Path.GetFileName(file.Original), RestoreScope(under)) switch
        {
            NoReplaceRename.Renamed => c.Files.FlushFolder(folder) is FolderFlush.Done ? string.Empty : $"{file.Original} was restored but its folder could not be flushed",
            NoReplaceRename.NameTaken => $"{file.Original} appeared while it was restored; it was not replaced",
            NoReplaceRename.Refused refused => $"{file.Original} could not be given its name ({refused.Why})",
            _ => $"{file.Original} could not be given its name",
        };

    /// <summary>The archived copy's bytes into the created file, hashed on the way; flushed; its last write the restore time.</summary>
    private static string Streamed(MoveContext c, string sideFolder, IndexFile file, ExclusiveFile.Created created)
    {
        using var target = created.Stream;
        if (c.Files.OpenSource(sideFolder, Path.Combine(sideFolder, file.Archived)) is not SourceOpen.Opened opened)
        {
            return $"{file.Archived} could not be opened in the base";
        }

        using (var source = opened.Stream)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[RestoreBuffer];
            long length = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                length += read;
                if (length > file.Bytes)
                {
                    return $"{file.Archived} is longer than its index line says ({file.Bytes} bytes); it changed after its check and is not restored";
                }

                sha.AppendData(buffer, 0, read);
                target.Write(buffer, 0, read);
                c.Step(MoveSteps.RestoreChunk);
            }

            if (Convert.ToHexStringLower(sha.GetHashAndReset()) != file.Sha256 || length != file.Bytes)
            {
                return $"{file.Archived} changed after its check; it is not restored";
            }
        }

        target.Flush(flushToDisk: true);
        File.SetLastWriteTimeUtc(target.SafeFileHandle, c.Clock.GetUtcNow().UtcDateTime);
        return string.Empty;
    }

    private static int RestoreBuffer => Tuning.Current.Int(ConfigKeys.Archive.CopyBufferKib) * 1024;
}
