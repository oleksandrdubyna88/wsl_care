using System.Text.Json.Serialization;

namespace WslCare.Core.Worktrees;

/// <summary>A count and the logical bytes it holds.</summary>
public sealed record Tally(int Count, long Bytes);

/// <summary>The folders idle for more than <paramref name="Days"/> days (<c>worktrees.idleDays</c>).</summary>
public sealed record IdleOver(int Days, int Count, long SizeBytes);

/// <summary>Build output a cleanup would keep, and why (<paramref name="Reason"/> is one of a closed set of sentences).</summary>
public sealed record RetainedGroup(string Reason, int Count, long Bytes);

/// <summary>Build output per bucket: <c>all</c> every build-output folder found; the three others the ones that passed every structural check, by where they lie.</summary>
public sealed record BuildOutputTotals(Tally All, Tally InIdleUnlocked, Tally InIdleLocked, Tally InOrphans, IReadOnlyList<RetainedGroup> Retained);

/// <summary>Folders the scan could not establish, and why (the distinct reasons).</summary>
public sealed record UnknownTotals(int Count, string Why);

/// <summary>One build-output folder a cleanup would keep, named (a file that is not a build artifact is named in <paramref name="Reason"/>).</summary>
public sealed record NamedRetention(string Folder, string Candidate, string Reason);

/// <summary>One root of the report.</summary>
public sealed record RootReport(
    string Path,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Refused,
    int Folders,
    int Worktrees,
    int Locked,
    int Orphans,
    int ForeignPointers,
    int MainClones,
    int NotGit,
    int Links,
    int TestLeftovers,
    long SizeBytes,
    IdleOver IdleOver,
    BuildOutputTotals BuildOutput,
    UnknownTotals Unknown,
    IReadOnlyList<NamedRetention> NamedRetentions);

/// <summary>One of the largest folders, by name — never its contents. In this story the size is the build output present, NOT a reclaimable figure.</summary>
public sealed record TopFolder(string Root, string Folder, string Kind, bool Locked, string Idle, long BuildOutputBytes, long SizeBytes);

/// <summary>What a cleanup could reclaim — absent until git has confirmed the candidates (E19.S4): <c>available: false</c> and why, never a byte figure.</summary>
public sealed record ReclaimableReport(bool Available, string Reason);

/// <summary><c>worktrees.json</c> (PLAN_disk_hygiene_windows.md §3.3): the report <c>worktrees scan</c> writes under <c>%LOCALAPPDATA%\wsl-care</c>.</summary>
public sealed record WorktreeReport(
    int SchemaVersion,
    DateTimeOffset ScannedAt,
    double ElapsedSeconds,
    bool Complete,
    IReadOnlyList<RootReport> Roots,
    IReadOnlyList<TopFolder> Top,
    ReclaimableReport Reclaimable);
