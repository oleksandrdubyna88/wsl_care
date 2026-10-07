using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;

namespace WslCare.Core.Archive;

/// <summary>What the reconcile at the start of a run did (plan §15r D3).</summary>
public sealed record ArchiveReconcileReport(int Indexed, int Dropped, int Resumed, int RenamedBack, int KeptAside, IReadOnlyList<string> Notes)
{
    public static ArchiveReconcileReport Empty { get; } = new(0, 0, 0, 0, 0, []);
}

/// <summary>
/// Plan §15r D3 — at the start of every run, under the side's lock, BEFORE selecting. From the in-flight file: a <c>copying</c> entry
/// whose unit the index holds moves to <c>archived</c>; one it does not hold is dropped — the source was never touched, its copies
/// stay without an index line (harmless: the next copy reuses an equal one, <c>reconcile --scan</c> re-indexes) — and a <c>removing</c>
/// entry is resumed from its removal. Without it (risk consult 9/9.2, row 2): every file under a quarantine name that no
/// <c>removing</c> entry covers is RENAMED BACK, never replacing — never unlinked, because nothing says its commit point passed.
/// </summary>
public static class ArchiveReconcile
{
    public static ArchiveReconcileReport FromInflight(MoveContext c)
    {
        var report = ArchiveReconcileReport.Empty;
        foreach (var entry in c.Book.Entries.ToList())
        {
            report = entry.State switch
            {
                InflightStates.Copying => Copying(c, entry, report),
                InflightStates.Removing => Removing(c, entry, report),
                _ => report,
            };
        }

        return report;
    }

    /// <summary>A crash in phase 1: indexed → <c>archived</c>; not indexed → dropped.</summary>
    private static ArchiveReconcileReport Copying(MoveContext c, InflightEntry entry, ArchiveReconcileReport report)
    {
        var indexed = MonthIndex.Entries(c, entry.Agent, entry.Month)
            .Where(e => e.Key == entry.Key && e.Status == ArchiveIndex.Events.Archived && e.Verified)
            .OrderByDescending(e => e.ArchivedAtUtc)
            .FirstOrDefault();
        if (indexed is null)
        {
            c.Book.Drop(entry.EntryId);
            return report with { Dropped = report.Dropped + 1 };
        }

        c.Book.Replace(entry.EntryId, entry with { EntryId = indexed.EntryId, State = InflightStates.Archived, ArchivedAtUtc = indexed.ArchivedAtUtc });
        return report with { Indexed = report.Indexed + 1 };
    }

    /// <summary>A crash after the commit point: the removal resumed with the files the index holds.</summary>
    private static ArchiveReconcileReport Removing(MoveContext c, InflightEntry entry, ArchiveReconcileReport report)
    {
        var indexed = MonthIndex.Entries(c, entry.Agent, entry.Month).FirstOrDefault(e => e.EntryId == entry.EntryId);
        var outcome = indexed is null ? new RemoveOutcome.Kept("its index line is not readable now") : ArchiveRemove.Resume(c, entry, indexed);
        return outcome is RemoveOutcome.Removed
            ? report with { Resumed = report.Resumed + 1 }
            : report with { Notes = [.. report.Notes, $"{entry.Key}: the interrupted removal waits ({Why(outcome)})"] };
    }

    /// <summary>Every quarantined file of <paramref name="agent"/> no <c>removing</c> entry covers, renamed back without replacing.</summary>
    public static ArchiveReconcileReport RenameBack(MoveContext c, string agent, string under, IEnumerable<string> quarantined, ArchiveReconcileReport report)
    {
        var covered = c.Book.Entries.Where(e => e.State == InflightStates.Removing && e.Agent == agent).Select(e => ArchiveNames.QuarantineMark + e.QuarantineRun).ToList();
        foreach (var relative in quarantined.Where(q => !covered.Any(mark => q.EndsWith(mark, StringComparison.Ordinal))))
        {
            report = Back(c, agent, under, relative, report);
        }

        return report;
    }

    private static ArchiveReconcileReport Back(MoveContext c, string agent, string under, string relative, ArchiveReconcileReport report)
    {
        var name = Path.GetFileName(relative);
        var back = c.Files.RenameBack(under, Path.Combine(under, relative), QuarantineCount.Original(name), new DeletionScope(under, "A13", DeletionPermit.ArchiveQuarantine));
        return back is NoReplaceRename.Renamed or NoReplaceRename.Gone ? report with { RenamedBack = report.RenamedBack + 1 }
            : back is NoReplaceRename.NameTaken && SplitRemoved(c, agent, under, relative) ? report with { RenamedBack = report.RenamedBack + 1 }
            : report with { KeptAside = report.KeptAside + 1, Notes = [.. report.Notes, $"{relative} stays under its quarantine name ({Answer(back)})"] };
    }

    /// <summary>Review B1's split: the agent wrote a NEW file at the original name, so the quarantined one cannot go back — it is
    /// removed only when an archived copy of an index line of this side (the months of this agent's in-flight entries) holds exactly
    /// its bytes; the seam hashes that copy and the quarantined file before it acts. Otherwise both stay.</summary>
    private static bool SplitRemoved(MoveContext c, string agent, string under, string relative)
    {
        var original = relative[..relative.IndexOf(ArchiveNames.QuarantineMark, StringComparison.Ordinal)];
        var path = Path.Combine(under, relative);
        var months = c.Book.Entries.Where(e => e.Agent == agent).Select(e => e.Month).Distinct(StringComparer.Ordinal);
        var candidates = months.SelectMany(m => MonthIndex.Entries(c, agent, m).Where(e => e.Verified).SelectMany(e => e.Files.Where(f => f.Original == original).Select(f => (Month: m, File: f))));
        return candidates.Any(k => c.Files.RemoveVerified(under, path, k.File.Sha256, ArchivedCopy(c, agent, k.Month, k.File), new DeletionScope(under, "A13", DeletionPermit.ArchiveRemoval)) is VerifiedRemoval.Removed);
    }

    private static string ArchivedCopy(MoveContext c, string agent, string month, IndexFile file) =>
        Path.Combine([c.BaseFolder, .. ArchiveCopy.Levels(agent, month, c.Side, []), .. file.Archived.Split('/')]);

    private static string Answer(NoReplaceRename back) => back switch
    {
        NoReplaceRename.NameTaken => "the agent wrote a new file at its original name; both are kept",
        NoReplaceRename.Refused refused => refused.Why,
        _ => "not renamed",
    };

    private static string Why(RemoveOutcome outcome) => outcome switch
    {
        RemoveOutcome.Kept kept => kept.Why,
        RemoveOutcome.Damaged damaged => damaged.Why,
        RemoveOutcome.Superseded superseded => superseded.Why,
        RemoveOutcome.Dropped dropped => dropped.Why,
        _ => "unknown",
    };
}

/// <summary>A month index of this side read from the base: missing is empty, past <c>archive.maxIndexBytes</c> refused.</summary>
public static class MonthIndex
{
    public static IReadOnlyList<IndexEntry> Entries(MoveContext c, string agent, string month) => ArchiveIndex.Merge(Read(c, agent, month).Records);

    public static IndexRead Read(MoveContext c, string agent, string month)
    {
        if (c.Files.OpenExistingFolderBeneath(c.BaseFolder, ArchiveCopy.Levels(agent, month, c.Side, [])) is not FolderBeneath.Ready { Folder: var folder })
        {
            return new IndexRead([], 0);
        }

        using (folder)
        {
            return c.Files.ReadCapped(folder, ArchiveIndex.FileName, Tuning.Current.Int(ConfigKeys.Archive.MaxIndexBytes)) is FileReadResult.Content content
                ? ArchiveIndex.Read(content.Bytes, c.Key)
                : new IndexRead([], 0);
        }
    }
}
