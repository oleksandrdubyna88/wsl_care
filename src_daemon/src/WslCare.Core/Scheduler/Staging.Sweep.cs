using System.Globalization;

using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;

namespace WslCare.Core.Scheduler;

/// <summary>The sweep and the removal of staging folders (PLAN_windows_runner.md §3.2, §3.10, §4).</summary>
/// <remarks>
/// <para>Only a FOLDER whose name is a digest (64 lower-case hex digits, <see cref="StageRecords.Sha256Shape"/>) is ever touched; any
/// other entry of the root — a person's folder, a file, a link (never followed), an upper-case name — is left as it is and named.</para>
/// <para><b>The keep rule</b> (the review of WR-D2): only a VERIFIED folder (<see cref="StagedCopies.Verify"/>: an agreeing
/// <c>stage.json</c>, bytes that hash to the name — any release) is ever protected; an unverified one never is, whatever its name.
/// <c>tasks stage</c> keeps EVERY verified folder and sweeps the unverified ones only — the folder the registered tasks name is a
/// verified one, whichever it is (one declined registration, a skewed clock). The <c>tasks run</c> start sweep runs FROM the registered
/// copy, so it knows which one that is: it keeps its own digest and the newest verified digest (a future <c>stagedAtUtc</c> counts as
/// now, and loses a tie to a genuine stamp) and retires the other verified copies. <c>--remove</c> keeps nothing.</para>
/// <para>A folder goes exe FIRST, through the deletion policy (<see cref="IFileSystem.DeleteFile"/>): an exe that is executing
/// cannot be deleted (Windows refuses the image of a running process, as it refuses a file held open without delete sharing), so
/// the folder is KEPT whole — its <c>stage.json</c> too — named, and retried by the next stage, remove or <c>tasks run</c> start. Only
/// then is the rest of the folder removed (<see cref="IFileSystem.DeleteDirectory"/>, judged inside the staging root).</para>
/// <para>Every sweep and removal takes THE run lock without waiting; a held lock does nothing and says so.</para>
/// </remarks>
public static partial class Staging
{
    /// <summary>The <c>tasks run</c> start's sweep (§3.10; WR-D3/D4 call it with the digest the running copy is staged under): every
    /// digest folder but the running one and the newest verified one — each kept only when it verifies — removed oldest first.</summary>
    /// <param name="ownDigest">The digest folder the sweeping process runs from; empty when it runs from none.</param>
    public static StagingSweep Sweep(StagingAccess access, string ownDigest) =>
        Locked(access.Place, access.Files, () => Swept(access, new KeepRule(ownDigest, KeepAllVerified: false, Keeps: true)), Skipped);

    /// <summary><c>tasks stage --remove</c>: every digest folder (the keep rule does not apply), then the root itself when that left it
    /// empty — a non-recursive removal judged inside the state directory, which itself is never removed.</summary>
    public static StageRemoveReport Remove(StagingAccess access) =>
        Locked(access.Place, access.Files, () => RemoveLocked(access), why => new StageRemoveReport(Core.SchemaVersion.Current, StageOutcomes.Busy, why, access.Place.Root, false, [], [], []));

    private static StageRemoveReport RemoveLocked(StagingAccess access)
    {
        var place = access.Place;
        var sweep = Swept(access, new KeepRule(string.Empty, KeepAllVerified: false, Keeps: false));
        var empty = access.Files.DirectoryExists(place.Root) && sweep.Kept.Count == 0 && sweep.Untouched.Count == 0;
        var rootRemoved = empty && access.Archive.RemoveEmptyFolder(place.StateDirectory, place.Root, new DeletionScope(place.StateDirectory, Action)) is VerifiedRemoval.Removed;
        return new StageRemoveReport(Core.SchemaVersion.Current, StageOutcomes.Removed, string.Empty, place.Root, rootRemoved, sweep.Swept, sweep.Kept, sweep.Untouched);
    }

    /// <summary>The stage's sweep, under the stage's own hold of the run lock: the unverified folders only — every verified copy stays
    /// (M2: the one the registered tasks name is among them).</summary>
    private static StagingSweep SweepAfterStage(StagingAccess access, string ownDigest) =>
        Swept(access, new KeepRule(ownDigest, KeepAllVerified: true, Keeps: true));

    /// <summary><paramref name="act"/> under THE run lock, taken without waiting; <paramref name="busy"/>'s answer when it is held.</summary>
    private static T Locked<T>(StagingPlace place, IFileSystem files, Func<T> act, Func<string, T> busy)
    {
        if (files.TryLockExclusive(place.RunLockFile) is not ExclusiveLock.Held held)
        {
            return busy(BusyReason(place));
        }

        using (held.Handle)
        {
            return act();
        }
    }

    private static StagingSweep Skipped(string why) => new([], [], [], [], why);

    /// <summary>What a sweep keeps: <paramref name="Own"/> when it verifies, and either every verified folder or the newest one;
    /// nothing at all when <paramref name="Keeps"/> is false.</summary>
    private sealed record KeepRule(string Own, bool KeepAllVerified, bool Keeps);

    private static StagingSweep Swept(StagingAccess access, KeepRule rule)
    {
        var place = access.Place;
        var entries = access.Files.ListEntries(place.Root);
        var checks = entries.Where(IsDigestFolder).ToDictionary(e => e.Name, e => StagedCopies.Verify(place.Root, e.Name, access.Files, access.Archive, access.MaxStageBytes), StringComparer.Ordinal);
        var retained = rule.Keeps ? Retained(checks, rule, access.Now) : [];
        var keep = retained.Select(c => c.Digest).Append(Verified(checks, rule.Own) ? rule.Own : string.Empty).ToHashSet(StringComparer.Ordinal);
        var removals = entries
            .Where(e => IsDigestFolder(e) && !keep.Contains(e.Name))
            .OrderBy(e => e.LastWriteUtc)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => Removed(place, e.Name, access.Files, WhatItWas(checks[e.Name])))
            .ToList();
        return new StagingSweep(
            [.. removals.Where(r => r.Gone).Select(r => r.Folder)],
            [.. removals.Where(r => !r.Gone).Select(r => r.Folder)],
            [.. retained.Select(c => new StagingFolder(c.Digest, PathRules.Windows.Join(place.Root, c.Digest), RetainedWhy(c, rule)))],
            [.. entries.Where(e => !IsDigestFolder(e)).Select(e => PathRules.Windows.Join(place.Root, e.Name))],
            string.Empty);
    }

    private static bool Verified(IReadOnlyDictionary<string, StagedCheck> checks, string digest) => checks.TryGetValue(digest, out var check) && check.Registrable;

    /// <summary>The verified folders the rule keeps besides the sweeper's own: every one at stage time, the newest one at a run start.</summary>
    private static List<StagedCheck> Retained(IReadOnlyDictionary<string, StagedCheck> checks, KeepRule rule, DateTimeOffset now)
    {
        var others = checks.Values.Where(c => c.Registrable);
        var kept = rule.KeepAllVerified ? others : NewestOf(others, now);
        return [.. kept.Where(c => c.Digest != rule.Own).OrderBy(c => c.Digest, StringComparer.Ordinal)];
    }

    /// <summary>The newest by <c>stagedAtUtc</c> clamped to <paramref name="now"/> (a future stamp counts as now), a genuine stamp first
    /// on a tie, then by name — or none.</summary>
    private static IEnumerable<StagedCheck> NewestOf(IEnumerable<StagedCheck> folders, DateTimeOffset now) =>
        folders
            .OrderByDescending(c => c.StagedAtUtc > now ? now : c.StagedAtUtc)
            .ThenBy(c => c.StagedAtUtc > now)
            .ThenBy(c => c.Digest, StringComparer.Ordinal)
            .Take(1);

    private static string RetainedWhy(StagedCheck check, KeepRule rule) =>
        string.Create(CultureInfo.InvariantCulture, $"{(rule.KeepAllVerified ? "a verified copy" : "the newest verified copy")} (release {check.Version}, staged {check.StagedAtUtc:u}): a registered task may still name it; a tasks run from the newest copy retires it");

    private static bool IsDigestFolder(FileEntry entry) => entry.Kind == EntryKind.Directory && StageRecords.Sha256Shape().IsMatch(entry.Name);

    /// <summary>One folder's removal: <paramref name="Gone"/> when it went, the folder with what it was or why it stays.</summary>
    private sealed record Removal(bool Gone, StagingFolder Folder);

    /// <summary>The folder removed exe first (an exe in use keeps it whole), then the rest — <paramref name="what"/> is what it was.</summary>
    private static Removal Removed(StagingPlace place, string digest, IFileSystem files, string what)
    {
        var folder = PathRules.Windows.Join(place.Root, digest);
        var exe = PathRules.Windows.Join(folder, StagedCopies.ExeName);
        var problem = files.FileExists(exe) && Deleted(() => files.DeleteFile(exe, Scope(place)), isExe: true) is { Length: > 0 } held ? held
            : Deleted(() => files.DeleteDirectory(folder, Scope(place)), isExe: false);
        return problem.Length == 0
            ? new Removal(true, new StagingFolder(digest, folder, what))
            : new Removal(false, new StagingFolder(digest, folder, $"{what}; it stays: {problem}"));
    }

    /// <summary>Empty when the delete went; the policy's refusal or why the file system refused it otherwise.</summary>
    /// <param name="isExe">The delete of the exe itself: Windows refuses a running image with access denied.</param>
    private static string Deleted(Func<DeletionVerdict> delete, bool isExe)
    {
        try
        {
            return delete() is DeletionVerdict.Refused refused ? refused.Reason : string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return InUse(e, isExe)
                ? $"it is in use — a run still executes it, or it is held open ({e.Message}); the next stage, remove or tasks run start retries"
                : $"it could not be removed ({e.Message}); the next stage, remove or tasks run start retries";
        }
    }

    /// <summary>A sharing violation (<see cref="BeneathWrites.WindowsSharing"/>, the Win32 code the archive's seam already names) on any
    /// file, or access denied on the exe — a running image (the review of WR-D2, m1); anything else is a plain failure, never called
    /// "in use".</summary>
    private static bool InUse(Exception e, bool isExe) => (e.HResult & 0xFFFF) == BeneathWrites.WindowsSharing || (isExe && e is UnauthorizedAccessException);

    /// <summary>What a digest folder held, from its check: an older verified copy, or an unverified one and why.</summary>
    private static string WhatItWas(StagedCheck check) => check.Registrable
        ? string.Create(CultureInfo.InvariantCulture, $"an older copy (release {check.Version}, staged {check.StagedAtUtc:u})")
        : $"unverified: {check.Reason}";
}
