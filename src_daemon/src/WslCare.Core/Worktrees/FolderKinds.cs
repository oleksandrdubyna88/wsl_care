using System.Text;

using WslCare.Core.Collectors;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Worktrees;

/// <summary>
/// The classifier of E19.S3 (PLAN_disk_hygiene_windows.md §3.3 "What each direct child folder of a root is"): what a direct child folder of
/// a root is, answered by bounded, no-link FILE READS alone — never by starting git, which is why a main clone, a worktree whose admin
/// folder points back, an orphan, a worktree made inside WSL (<c>/mnt/&lt;letter&gt;/…</c>, translated; any other POSIX path FOREIGN) and a
/// folder that is not git at all are told apart by the pointer files and nothing else. A pointer that cannot be read is
/// <see cref="FolderKind.Unreadable"/>, never an orphan: only what is positively known to point nowhere is a candidate.
/// </summary>
public static class FolderKinds
{
    /// <summary>The prefix of every folder a test creates outside its process (the shared test-leftovers rule): flagged, counted, never swept here.</summary>
    public const string TestLeftoverPrefix = "testsToDelete-";

    private const string GitName = ".git";

    public static FolderFacts Classify(IFileSystem files, IHostPaths paths, WorktreeSettings settings, string root, FileEntry child)
    {
        var path = paths.Rules.Join(root, child.Name);
        var leftover = child.Name.StartsWith(TestLeftoverPrefix, StringComparison.OrdinalIgnoreCase);
        var facts = new FolderFacts(child.Name, path, FolderKind.NotGit, false, string.Empty, leftover, string.Empty, "no .git in it");
        if (child.Kind == EntryKind.Link)
        {
            return facts with { Kind = FolderKind.Link, Why = "the folder is a link; it is never entered" };
        }

        var git = paths.Rules.Join(path, GitName);
        return files.DirectoryExists(git) ? facts with { Kind = FolderKind.MainClone, Why = "its .git is a folder: a main clone" }
            : files.FileExists(git) ? FromPointerFile(files, paths.Rules, settings, root, path, git, facts)
            : facts;
    }

    /// <summary>The <c>.git</c> FILE read (capped, no link on the way from the root) and followed.</summary>
    private static FolderFacts FromPointerFile(IFileSystem files, PathRules rules, WorktreeSettings settings, string root, string folder, string gitFile, FolderFacts facts) =>
        ReadPointer(files, settings, root, gitFile, GitPointers.Parse) switch
        {
            Reading<string>.Unavailable bad => facts with { Kind = FolderKind.Unreadable, Why = $"its .git file {bad.Reason}" },
            Reading<string>.Available pointer => Follow(files, rules, settings, folder, gitFile, pointer.Value, facts),
            _ => throw new System.Diagnostics.UnreachableException("Reading is a closed set"),
        };

    private static Reading<string> ReadPointer(IFileSystem files, WorktreeSettings settings, string beneath, string file, Func<string, Reading<string>> parse) =>
        files.ReadUserFile(file, settings.MaxSmallFileBytes, 0, beneath) switch
        {
            FileReadResult.Content content => parse(Encoding.UTF8.GetString(content.Bytes)),
            FileReadResult.Missing => Reading.Missing<string>("is not there"),
            FileReadResult.Unreadable unreadable => Reading.Missing<string>($"could not be read: {unreadable.Reason}"),
            _ => throw new System.Diagnostics.UnreachableException("FileReadResult is a closed set"),
        };

    private static FolderFacts Follow(IFileSystem files, PathRules rules, WorktreeSettings settings, string folder, string gitFile, string pointed, FolderFacts facts) =>
        GitPointers.Resolve(pointed, folder, settings.AutomountRoots) switch
        {
            Reading<string>.Unavailable foreign => facts with { Kind = FolderKind.ForeignPointer, Why = $"its .git file points at {pointed}, which the Windows side cannot reach: a worktree made elsewhere, never a candidate" },
            Reading<string>.Available admin => Admin(files, rules, settings, gitFile, admin.Value, pointed, facts),
            _ => throw new System.Diagnostics.UnreachableException("Reading is a closed set"),
        };

    /// <summary>The admin folder the pointer names: it must exist, and its own <c>gitdir</c> must point back at this folder's <c>.git</c>.</summary>
    private static FolderFacts Admin(IFileSystem files, PathRules rules, WorktreeSettings settings, string gitFile, string admin, string pointed, FolderFacts facts)
    {
        if (!files.DirectoryExists(admin))
        {
            return facts with { Kind = FolderKind.Orphan, AdminFolder = admin, Why = $"its .git file points at {admin}, which does not exist" };
        }

        var withAdmin = facts with { AdminFolder = admin };
        var back = ReadPointer(files, settings, admin, rules.Join(admin, "gitdir"), GitPointers.ParseBare);
        return back switch
        {
            Reading<string>.Available { Value: var target } => PointsBack(files, rules, settings, gitFile, admin, target, withAdmin),
            Reading<string>.Unavailable { Reason: "is not there" } => withAdmin with { Kind = FolderKind.Orphan, Why = $"its admin folder {admin} holds no gitdir file" },
            Reading<string>.Unavailable bad => withAdmin with { Kind = FolderKind.Unreadable, Why = $"the gitdir file in its admin folder {bad.Reason}" },
            _ => throw new System.Diagnostics.UnreachableException("Reading is a closed set"),
        };
    }

    private static FolderFacts PointsBack(IFileSystem files, PathRules rules, WorktreeSettings settings, string gitFile, string admin, string target, FolderFacts facts)
    {
        var there = GitPointers.Resolve(target, admin, settings.AutomountRoots);
        return there is Reading<string>.Available { Value: var resolved } && rules.PathEquals(resolved, gitFile)
            ? Locked(files, rules, settings, admin, facts with { Kind = FolderKind.Worktree, Why = "its .git file and its admin folder point at each other" })
            : NotBack(files, admin, target, there, facts);
    }

    /// <summary>The admin folder does not point at THIS .git file. Only a target that is not there makes an orphan; a .git file that EXISTS under another
    /// spelling (a link, an alias, a short name) may be this very worktree, so it is unreadable — never an orphan, never a candidate (Fable review, E19.S3 condition 3).</summary>
    private static FolderFacts NotBack(IFileSystem files, string admin, string target, Reading<string> there, FolderFacts facts) =>
        there is Reading<string>.Available { Value: var resolved } && files.FileExists(resolved)
            ? facts with { Kind = FolderKind.Unreadable, Why = $"its admin folder {admin} points at a .git file that exists under another spelling ({target}); it may be this folder, so it is not judged" }
            : facts with { Kind = FolderKind.Orphan, Why = $"its admin folder {admin} points at {target}, not at this folder" };

    /// <summary>A <c>locked</c> file in the admin folder is how <c>git worktree lock</c> is stored; its text is the reason, capped and made printable.</summary>
    private static FolderFacts Locked(IFileSystem files, PathRules rules, WorktreeSettings settings, string admin, FolderFacts facts)
    {
        var lockFile = rules.Join(admin, "locked");
        return !files.FileExists(lockFile) ? facts : facts with { Locked = true, LockedReason = Reason(files.ReadUserFile(lockFile, settings.MaxSmallFileBytes, 0, admin), settings.MaxReasonChars) };
    }

    private static string Reason(FileReadResult read, int maxChars) => read switch
    {
        FileReadResult.Content content => Printable(Encoding.UTF8.GetString(content.Bytes), maxChars),
        FileReadResult.Unreadable => "(a reason too long to show)",
        _ => string.Empty,
    };

    private static string Printable(string text, int maxChars)
    {
        var clean = new string([.. text.Where(c => !char.IsControl(c))]).Trim();
        return clean.Length > maxChars ? clean[..maxChars] : clean;
    }
}
