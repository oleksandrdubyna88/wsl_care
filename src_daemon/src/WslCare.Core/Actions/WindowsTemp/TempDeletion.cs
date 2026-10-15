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
/// The delete walk of one candidate (PLAN_windows_runner.md §3.12; the story's plan round, finding 6): it enumerates each folder
/// WITHOUT following reparse points and checks each entry as it goes — an entry that is, or has become, a link stops the candidate
/// ("a link appeared"), and nothing beyond it is touched. Every delete goes through <see cref="IFileSystem"/>, so the deletion policy
/// judges it. A sharing violation or any other I/O failure is kept and counted, never retried in the same run.
/// </summary>
internal static class TempDeletion
{
    public static TreeDeletion DeleteTree(IFileSystem files, PathRules rules, DeletionScope scope, string folder, CancellationToken token)
    {
        var walk = new Walk(files, rules, scope, token);
        var emptied = walk.Folder(folder);
        return new TreeDeletion(walk.Bytes, walk.Kept, walk.Stop, walk.Cancelled, emptied);
    }

    /// <summary>One file — a top-level entry that is a file.</summary>
    public static TreeDeletion DeleteFile(IFileSystem files, DeletionScope scope, string path, long length)
    {
        var walk = new Walk(files, PathRules.Windows, scope, CancellationToken.None);
        var gone = walk.File(path, length);
        return new TreeDeletion(walk.Bytes, walk.Kept, walk.Stop, false, gone);
    }

    private sealed class Walk(IFileSystem files, PathRules rules, DeletionScope scope, CancellationToken token)
    {
        private readonly List<string> _kept = [];

        public long Bytes { get; private set; }

        public IReadOnlyList<string> Kept => _kept;

        public string Stop { get; private set; } = string.Empty;

        public bool Cancelled { get; private set; }

        private bool Stopped => Stop.Length > 0 || Cancelled;

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
            EntryKind.Directory => files.ReadLink(path) is LinkReadResult.NotALink ? Folder(path) : Halt($"a link appeared: {path}"),
            _ => File(path, entry.Length),
        };

        private bool Halt(string reason)
        {
            Stop = reason;
            return false;
        }

        public bool File(string path, long length)
        {
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
            try
            {
                return files.DeleteDirectory(path, scope) is DeletionVerdict.Refused refused ? Halt($"the deletion policy refused {path}: {refused.Reason}") : true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _kept.Add($"{path} ({e.Message})");
                return false;
            }
        }
    }
}
