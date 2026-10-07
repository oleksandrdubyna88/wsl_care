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
}

/// <summary>One session a restore looked at: what became of it and why.</summary>
public sealed record RestoredSession(string EntryId, string Agent, string Key, string Month, string Outcome, int Files, long Bytes, string Note);

/// <summary>What <c>archive restore</c> did.</summary>
public sealed record RestoreReport(int Restored, int AlreadyThere, int Refused, IReadOnlyList<RestoredSession> Sessions)
{
    public static RestoreReport Empty { get; } = new(0, 0, 0, []);

    public RestoreReport With(RestoredSession session) => this with
    {
        Restored = Restored + (session.Outcome == RestoreOutcomes.Restored ? 1 : 0),
        AlreadyThere = AlreadyThere + (session.Outcome == RestoreOutcomes.AlreadyThere ? 1 : 0),
        Refused = Refused + (session.Outcome == RestoreOutcomes.Refused ? 1 : 0),
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
    /// <summary>One entry asked for, with the agent it belongs to and that agent's layout root on this side.</summary>
    public sealed record Candidate(ArchiveTarget Target, IndexEntry Entry);

    public static RestoreReport Restore(MoveContext c, IReadOnlyList<Candidate> candidates, bool acceptUnverified) =>
        candidates.Aggregate(RestoreReport.Empty, (report, candidate) => report.With(One(c, candidate, acceptUnverified)));

    private static RestoredSession One(MoveContext c, Candidate candidate, bool acceptUnverified)
    {
        var (target, entry) = (candidate.Target, candidate.Entry);
        if (Unrestorable(c, target, entry, acceptUnverified) is { Length: > 0 } why)
        {
            return Session(entry, RestoreOutcomes.Refused, 0, why);
        }

        var plan = Planned(c, target.Under, entry);
        return plan.Problem.Length > 0 ? Session(entry, RestoreOutcomes.Refused, 0, plan.Problem)
            : plan.Missing.Count == 0 ? Session(entry, RestoreOutcomes.AlreadyThere, 0, "every file is already there with its archived bytes")
            : Written(c, target, entry, plan.Missing);
    }

    private static RestoredSession Session(IndexEntry entry, string outcome, long bytes, string note) =>
        new(entry.EntryId, entry.Agent, entry.Key, entry.Month, outcome, entry.Files.Count, bytes, note);

    /// <summary>Why the entry is never restored as it stands; empty when it may be.</summary>
    private static string Unrestorable(MoveContext c, ArchiveTarget target, IndexEntry entry, bool acceptUnverified) =>
        target.Under.Length == 0 ? $"{entry.Agent} keeps no session layout on this side ({target.Refusal})"
        : entry.Files.Count == 0 ? "its index holds no files for it"
        : !entry.Verified && !acceptUnverified ? "its index lines are not all this side's (unverified, or recovered by a scan); it is restored only with --accept-unverified"
        : c.Book.Entries.Any(e => e.EntryId == entry.EntryId || (e.Agent == entry.Agent && e.Key == entry.Key)) ? "it is on its way through an archive run (in the in-flight file); restore it after the run finished it"
        : LayoutProblem(target.Entry, entry);

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

    /// <summary>The files still to create, or why the session is refused — decided for EVERY file before any is written.</summary>
    private sealed record RestorePlan(IReadOnlyList<IndexFile> Missing, string Problem);

    private static RestorePlan Planned(MoveContext c, string under, IndexEntry entry)
    {
        var missing = new List<IndexFile>();
        foreach (var file in entry.Files)
        {
            var problem = CopyProblem(c, entry, file) is { Length: > 0 } damaged ? damaged : TargetProblem(c, under, file, missing);
            if (problem.Length > 0)
            {
                return new RestorePlan([], problem);
            }
        }

        return new RestorePlan(missing, string.Empty);
    }

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
                return ArchiveCopy.Sha256Of(opened.Stream) == file.Sha256 ? string.Empty : $"{file.Original} exists with other bytes (a live session of that name); nothing of it is restored";
            case SourceOpen.Refused refused:
                return $"{file.Original} cannot be checked ({refused.Why}); nothing of it is restored";
            default:
                return $"{file.Original} cannot be checked";
        }
    }

    /// <summary>Every missing file created; then the <c>restored</c> event and <c>restored.json</c>.</summary>
    private static RestoredSession Written(MoveContext c, ArchiveTarget target, IndexEntry entry, IReadOnlyList<IndexFile> missing)
    {
        long bytes = 0;
        foreach (var file in missing)
        {
            if (Created(c, target.Under, entry, file) is { Length: > 0 } failed)
            {
                return Session(entry, RestoreOutcomes.Refused, bytes, $"{failed}; the files restored before it stay (they hold the archived bytes)");
            }

            bytes += file.Bytes;
        }

        var restored = new IndexLine(ArchiveIndex.SchemaVersion, ArchiveIndex.Events.Restored, entry.EntryId, entry.Agent, c.Side, entry.Key, entry.Month, c.Clock.GetUtcNow(), c.Zone.Id, c.RunId, [], string.Empty);
        var unwritten = ArchiveCopy.AppendLine(c, entry.Agent, entry.Month, restored) is { Length: > 0 } index ? $"; its restored line could not be written ({index})" : string.Empty;
        var unrecorded = c.Book.AddRestored(new RestoredEntry(entry.EntryId, entry.Agent, entry.Key, entry.Month)) is { Length: > 0 } local ? $"; restored.json could not be written ({local})" : string.Empty;
        return Session(entry, RestoreOutcomes.Restored, bytes, $"{missing.Count} file(s) created{unwritten}{unrecorded}");
    }

    private static DeletionScope RestoreScope(string under) => new(under, "A19", DeletionPermit.RestoreIntoAgentFolder);

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
            var name = Path.GetFileName(file.Original);
            return c.Files.CreateExclusive(folder, name, scope) switch
            {
                ExclusiveFile.Created created => Filled(c, entry, file, folder, name, created),
                ExclusiveFile.Exists => $"{file.Original} appeared while it was restored; it was not replaced",
                ExclusiveFile.Refused refused => $"{file.Original} could not be created ({refused.Why})",
                _ => $"{file.Original} could not be created",
            };
        }
    }

    private static string Filled(MoveContext c, IndexEntry entry, IndexFile file, BeneathFolder folder, string name, ExclusiveFile.Created created)
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

        return c.Files.FlushFolder(folder) is FolderFlush.Done && c.Files.ReadBack(folder, name) is FileHash.Hashed back && back.Sha256 == file.Sha256
            ? string.Empty
            : $"{file.Original} did not read back equal to its archived copy";
    }

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
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                target.Write(buffer, 0, read);
                c.Step(MoveSteps.RestoreChunk);
            }

            if (Convert.ToHexStringLower(sha.GetHashAndReset()) != file.Sha256)
            {
                return $"{file.Archived} changed while it was read";
            }
        }

        target.Flush(flushToDisk: true);
        File.SetLastWriteTimeUtc(target.SafeFileHandle, c.Clock.GetUtcNow().UtcDateTime);
        return string.Empty;
    }

    private static int RestoreBuffer => Tuning.Current.Int(ConfigKeys.Archive.CopyBufferKib) * 1024;
}
