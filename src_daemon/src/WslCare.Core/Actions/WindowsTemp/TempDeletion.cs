using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;

namespace WslCare.Core.Actions.WindowsTemp;

/// <summary>What deleting one tree did.</summary>
/// <param name="Bytes">The logical bytes of the files that were deleted.</param>
/// <param name="Kept">What stayed and why — a file in use, a folder that was not empty.</param>
/// <param name="Stop">Why the walk stopped before its end (a link appeared, the policy refused); empty when it did not.</param>
/// <param name="Cancelled">A cancellation ended the walk where it was.</param>
/// <param name="Emptied">The whole tree is gone.</param>
internal sealed record TreeDeletion(long Bytes, IReadOnlyList<string> Kept, string Stop, bool Cancelled, bool Emptied);

/// <summary>
/// The delete walk of one candidate (PLAN_windows_runner.md §3.12; the story's plan round, finding 6; the Fable own review M1, M4): it
/// enumerates each folder WITHOUT following reparse points and checks each entry as it goes — an entry that is, or has become, a link
/// stops the candidate ("a link appeared"), and nothing beyond it is touched. Before EVERY delete the path's REAL location is asked
/// for and must be where the walk expects it (the real temp root plus the entry's relative path): a link swapped in anywhere on the
/// way after the entry was listed stops the candidate. A folder is removed only when EMPTY (<see cref="IFileSystem.DeleteEmptyDirectory"/>,
/// never recursively), so whatever appeared after the listing is never deleted unjudged: the folder stays, counted "kept in use".
/// Every delete goes through <see cref="IFileSystem"/> and so the deletion policy. A sharing violation or any other I/O failure is kept
/// and counted, never retried in the same run.
/// <para><b>Residual, stated:</b> the real-path check and the delete are two calls — a swap between them is not excluded, because the
/// seam has no handle-relative delete (<c>NtSetInformationFile</c> on an open handle). The window is microseconds, the deletion policy
/// still judges the path, and what a swap could reach is limited to what this account may delete anyway.</para>
/// </summary>
internal static class TempDeletion
{
    /// <param name="cutoff">Nothing whose later of write and creation time is newer than this is ever deleted: the scan judged the tree
    /// by what it saw, and anything created or refreshed after it (a new file in a sub-folder not yet listed) was never judged (the coai
    /// code round 56c596c6, finding 10).</param>
    public static TreeDeletion DeleteTree(IFileSystem files, PathRules rules, DeletionScope scope, string folder, DateTimeOffset cutoff, CancellationToken token)
    {
        var walk = new Walk(files, rules, scope, cutoff, token);
        var emptied = walk.Candidate(folder) && walk.Folder(folder);
        return new TreeDeletion(walk.Bytes, walk.Kept, walk.Stop, walk.Cancelled, emptied);
    }

    /// <summary>One file — a top-level entry that is a file.</summary>
    public static TreeDeletion DeleteFile(IFileSystem files, PathRules rules, DeletionScope scope, string path, long length)
    {
        var walk = new Walk(files, rules, scope, DateTimeOffset.MaxValue, CancellationToken.None);
        var gone = walk.File(path, length);
        return new TreeDeletion(walk.Bytes, walk.Kept, walk.Stop, false, gone);
    }

    private sealed class Walk(IFileSystem files, PathRules rules, DeletionScope scope, DateTimeOffset cutoff, CancellationToken token)
    {
        private readonly List<string> _kept = [];
        private readonly string _realRoot = files.ResolvePath(scope.Root) is RealPathResult.Resolved resolved ? resolved.Path : string.Empty;

        public long Bytes { get; private set; }

        public IReadOnlyList<string> Kept => _kept;

        public string Stop { get; private set; } = string.Empty;

        public bool Cancelled { get; private set; }

        private bool Stopped => Stop.Length > 0 || Cancelled;

        /// <summary>The candidate folder itself, judged BEFORE it is listed (the coai code round, finding 4): not a link, and really where its
        /// spelling puts it. Sub-folders and entries get the same two questions as they are met.</summary>
        public bool Candidate(string path) =>
            files.ReadLink(path) is LinkReadResult.NotALink && InPlace(path) ? true : Halt($"a link appeared: {path}");

        /// <summary>The folder's entries deleted, then the folder itself; <c>true</c> when it is gone.</summary>
        public bool Folder(string path)
        {
            var allGone = true;
            foreach (var entry in files.ListEntries(path))
            {
                if (Stopped || (Cancelled = token.IsCancellationRequested))
                {
                    return false;
                }

                allGone &= Entry(entry, rules.Join(path, entry.Name));
            }

            return !Stopped && allGone && RemoveFolder(path);
        }

        private bool Entry(FileEntry entry, string path) => entry.Kind switch
        {
            EntryKind.Link => Halt($"a link appeared: {path}"),
            _ when Newest(entry) > cutoff => Halt($"{path} is newer than the age limit (created or written after the scan): changed while deleting"),
            EntryKind.Directory => files.ReadLink(path) is LinkReadResult.NotALink && InPlace(path) ? Folder(path) : Halt($"a link appeared: {path}"),
            _ => File(path, entry.Length),
        };

        private static DateTimeOffset Newest(FileEntry entry) => entry.LastWriteUtc >= entry.CreatedUtc ? entry.LastWriteUtc : entry.CreatedUtc;

        private bool Halt(string reason)
        {
            Stop = reason;
            return false;
        }

        /// <summary>Whether <paramref name="path"/> really is where its spelling puts it: the real temp root plus the same relative path.
        /// A path that resolves anywhere else — a link anywhere on the way — is not this walk's to delete.</summary>
        private bool InPlace(string path) =>
            _realRoot.Length > 0
            && files.ResolvePath(path) is RealPathResult.Resolved real
            && rules.PathEquals(real.Path, rules.Join(_realRoot, [.. rules.Segments(path).Skip(rules.Segments(scope.Root).Count)]));

        public bool File(string path, long length)
        {
            if (!InPlace(path))
            {
                return Halt($"a link appeared: {path} no longer resolves to where it was listed");
            }

            try
            {
                if (files.DeleteFile(path, scope) is DeletionVerdict.Refused refused)
                {
                    return Halt($"the deletion policy refused {path}: {refused.Reason}");
                }

                Bytes += length;
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _kept.Add($"{path} ({e.Message})");
                return false;
            }
        }

        private bool RemoveFolder(string path)
        {
            if (!InPlace(path))
            {
                return Halt($"a link appeared: {path} no longer resolves to where it was listed");
            }

            try
            {
                return files.DeleteEmptyDirectory(path, scope) is DeletionVerdict.Refused refused ? Halt($"the deletion policy refused {path}: {refused.Reason}") : true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _kept.Add($"{path} ({e.Message})");
                return false;
            }
        }
    }
}
