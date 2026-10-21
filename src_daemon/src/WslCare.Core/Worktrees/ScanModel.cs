namespace WslCare.Core.Worktrees;

/// <summary>What the file reads of the classifier established about ONE direct child of a root.</summary>
/// <param name="Name">The folder's name.</param>
/// <param name="Path">Its full Windows path.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Locked">The admin folder holds a <c>locked</c> file (how <c>git worktree lock</c> is stored); only a worktree can be.</param>
/// <param name="LockedReason">The lock's text, capped and made printable; empty when none or unlocked.</param>
/// <param name="TestLeftover">The folder's name starts with the test-leftover prefix — a flag, counted, never swept by this plan.</param>
/// <param name="AdminFolder">The admin folder the pointer names, as a Windows path; empty when there is none to name.</param>
/// <param name="Why">Why it is what it is, in one sentence (an orphan: what its pointer names; unreadable: what could not be read).</param>
public sealed record FolderFacts(string Name, string Path, FolderKind Kind, bool Locked, string LockedReason, bool TestLeftover, string AdminFolder, string Why);

/// <summary>How idle a folder is — a three-way fact, because "could not tell" is never "idle".</summary>
public enum IdleKind
{
    Idle,
    Active,
    Unknown,
}

/// <summary>A folder's idleness: <paramref name="Newest"/> is the newest write found (<see cref="DateTimeOffset.MinValue"/> when none was).</summary>
public sealed record IdleState(IdleKind Kind, string Reason, DateTimeOffset Newest);

/// <summary>One build-output folder found structurally (<c>bin</c>/<c>obj</c> beside a project file, a top-most <c>node_modules</c> beside a
/// <c>package.json</c>), measured by its own walk. <paramref name="Retained"/> is empty for a candidate that passes every structural check.</summary>
/// <param name="Path">Its full path.</param>
/// <param name="Relative">Its path from the worktree folder.</param>
/// <param name="Name">Its folder name, lower case (<c>bin</c>, <c>obj</c>, <c>node_modules</c>).</param>
/// <param name="Bytes">Logical bytes (file lengths) of what the walk saw.</param>
/// <param name="Files">How many files.</param>
/// <param name="Retained">Why it is NOT eligible (a nested repository; a file that is not a build artifact, named; its walk unfinished); empty when eligible.</param>
public sealed record BuildCandidate(string Path, string Relative, string Name, long Bytes, long Files, string Retained)
{
    /// <summary>What git said (E19.S4): <see cref="CandidateGit.NotAsked"/> until the scan asks — and it asks only a candidate that passed every
    /// structural check in a folder that is otherwise eligible (idle, not locked), because the first reason a candidate stays is the one reported.</summary>
    public CandidateGit Git { get; init; } = CandidateGit.NotAsked;
}

/// <summary>What git's three questions came to for one candidate — a closed set.</summary>
public abstract record CandidateGit
{
    private CandidateGit()
    {
    }

    /// <summary>Not asked: another check already keeps it, or the scan has no git.</summary>
    public static readonly CandidateGit NotAsked = new NotAskedState();

    /// <summary>Git confirms: ignored, and (a worktree) no tracked file inside. <paramref name="TrackedFilesChecked"/> is false for an orphan.</summary>
    public sealed record Confirmed(bool TrackedFilesChecked) : CandidateGit;

    /// <summary>Git did not confirm, or could not be asked; <paramref name="Reason"/> is one of <see cref="GitReasons"/>' sentences.</summary>
    public sealed record Retained(string Reason) : CandidateGit;

    private sealed record NotAskedState : CandidateGit;
}

/// <summary>One walked folder (a worktree or an orphan): its facts, idleness, logical size and build-output candidates.</summary>
public sealed record FolderScan(FolderFacts Facts, IdleState Idle, long Bytes, IReadOnlyList<BuildCandidate> Candidates)
{
    /// <summary>Why the walk did not establish the folder (a budget, an unreadable entry); empty when it did.</summary>
    public string UnknownWhy => Idle.Kind == IdleKind.Unknown ? Idle.Reason : string.Empty;
}

/// <summary>One root: refused with its reason, or the folders found in it.</summary>
/// <param name="Path">The root as configured.</param>
/// <param name="Refused">The rule that now refuses it and why (a hand-edited bad root); empty when it was scanned.</param>
/// <param name="Facts">Every direct child folder, classified (walked or not).</param>
/// <param name="Walked">The worktree and orphan folders, walked.</param>
public sealed record RootScan(string Path, string Refused, IReadOnlyList<FolderFacts> Facts, IReadOnlyList<FolderScan> Walked);

/// <summary>What one scan found, in memory; <see cref="WorktreeReports.From"/> makes the file of it.</summary>
public sealed record WorktreeScanResult(DateTimeOffset ScannedAt, TimeSpan Elapsed, bool Complete, IReadOnlyList<RootScan> Roots)
{
    /// <summary>The scan had git to ask (E19.S4). Without it no candidate is confirmed and the report's reclaimable stays unavailable.</summary>
    public bool GitAsked { get; init; }
}
