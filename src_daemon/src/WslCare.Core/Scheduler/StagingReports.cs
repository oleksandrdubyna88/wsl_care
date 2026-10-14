using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Scheduler;

/// <summary>What <c>tasks stage</c> did — a closed set of words.</summary>
public static class StageOutcomes
{
    /// <summary>A new copy was made, read back equal and recorded in its <c>stage.json</c>.</summary>
    public const string Staged = "staged";

    /// <summary>A copy of the same digest was already there and verifies; nothing was written to it.</summary>
    public const string Reused = "reused";

    /// <summary>No verified copy: the reason says why; no <c>stage.json</c> was written and nothing was swept.</summary>
    public const string Refused = "refused";

    /// <summary>Another run holds the run lock: nothing was done; try again (never waited on).</summary>
    public const string Busy = "busy";

    /// <summary><c>--remove</c> ran (what it removed and kept is in its lists).</summary>
    public const string Removed = "removed";
}

/// <summary>One staging folder a sweep or a removal acted on, or kept, and why.</summary>
/// <param name="Digest">Its name: the 64 lower-case hex digits of the copy it holds.</param>
/// <param name="Path">The folder.</param>
/// <param name="Why">One sentence: what it was (an older copy, an unverified one), or why it stays.</param>
public sealed record StagingFolder(string Digest, string Path, string Why);

/// <summary>What one sweep of the staging root did.</summary>
/// <param name="Swept">The digest folders removed.</param>
/// <param name="Kept">The digest folders that could not be removed (an exe still executing), each with the reason.</param>
/// <param name="Retained">The verified digest folders the keep rule kept, besides the sweeper's own, each with the reason.</param>
/// <param name="Untouched">The entries of the root that are not digest folders — never touched, named so a person sees why the
/// root is not empty.</param>
/// <param name="Busy">Why the sweep did not run (another run holds the run lock; the next start retries); empty when it ran.</param>
public sealed record StagingSweep(IReadOnlyList<StagingFolder> Swept, IReadOnlyList<StagingFolder> Kept, IReadOnlyList<StagingFolder> Retained, IReadOnlyList<string> Untouched, string Busy);

/// <summary><c>tasks stage --json</c> (WR-D2 of PLAN_windows_runner.md §3.2).</summary>
/// <param name="Outcome">One of <see cref="StageOutcomes"/>.</param>
/// <param name="Reason">One sentence: what was done, or why nothing was.</param>
/// <param name="Digest">The SHA-256 of the copy as it was read back (staged), of the copy found (reused), or of the source when
/// the refusal came after it was hashed; empty otherwise.</param>
/// <param name="Path">The staged exe (<c>&lt;stagingRoot&gt;\&lt;digest&gt;\wsl-care.exe</c>); empty when there is none.</param>
/// <param name="Version">The release its <c>stage.json</c> names.</param>
/// <param name="Source">The file the copy was made from (this process's own exe).</param>
public sealed record StageReport(
    int SchemaVersion,
    string Outcome,
    string Reason,
    string Digest,
    string Path,
    string Version,
    string Source,
    IReadOnlyList<StagingFolder> Swept,
    IReadOnlyList<StagingFolder> Kept,
    IReadOnlyList<StagingFolder> Retained);

/// <summary><c>tasks stage --remove --json</c> (WR-D2 of PLAN_windows_runner.md §3.2).</summary>
/// <param name="Outcome"><see cref="StageOutcomes.Removed"/>, or <see cref="StageOutcomes.Busy"/> (nothing was done).</param>
/// <param name="Reason">Why it was busy; empty when it ran.</param>
/// <param name="Root">The staging root.</param>
/// <param name="RootRemoved">The root itself was removed (it was empty once every digest folder had gone).</param>
public sealed record StageRemoveReport(
    int SchemaVersion,
    string Outcome,
    string Reason,
    string Root,
    bool RootRemoved,
    IReadOnlyList<StagingFolder> Removed,
    IReadOnlyList<StagingFolder> Kept,
    IReadOnlyList<string> Untouched);

/// <summary>Where the staged copies live: the staging root, the state directory that holds it, and THE run lock every stage,
/// removal and sweep takes without waiting (the review of WR-D2, m4: two stages of one digest are serialised).</summary>
public sealed record StagingPlace(string Root, string StateDirectory, string RunLockFile)
{
    public static StagingPlace For(WindowsHostPaths paths) => new(StagedCopies.Root(paths), paths.StateDirectory, paths.RunLockFile);
}

/// <summary>What a sweep or a removal reaches the disk through, and when it runs.</summary>
/// <param name="MaxStageBytes">The most bytes of a <c>stage.json</c> read (<c>records.maxStateFileBytes</c>).</param>
/// <param name="Now">The time a future <c>stagedAtUtc</c> is clamped to when the newest copy is chosen.</param>
public sealed record StagingAccess(StagingPlace Place, IFileSystem Files, IArchiveFiles Archive, int MaxStageBytes, DateTimeOffset Now);

/// <summary>One <c>tasks stage</c>: the copy made from <paramref name="Source"/>.</summary>
/// <param name="OwnVersion">This build's version text; <c>stage.json</c> records its release.</param>
/// <param name="MaxStageBytes">The most bytes of a <c>stage.json</c> read (<c>records.maxStateFileBytes</c>).</param>
public sealed record StageInput(StagingPlace Place, string Source, string OwnVersion, DateTimeOffset Now, int MaxStageBytes, IFileSystem Files, IArchiveFiles Archive)
{
    public StagingAccess Access => new(Place, Files, Archive, MaxStageBytes, Now);
}
