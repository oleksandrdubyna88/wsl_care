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

/// <summary>
/// What a cleanup would actually be allowed to delete (PLAN_disk_hygiene_windows.md §3.3, owner 2026-10-14): the logical bytes of the candidates
/// that pass EVERY check — structural, idle, unlocked, and git's confirmation — as <paramref name="A24"/> (worktrees) and
/// <paramref name="A24Orphans"/> (orphans: ignore-confirmed only, their tracked files NOT checked), with everything else counted under
/// <paramref name="Retained"/> by reason. <see cref="Available"/> is false, and the figures absent, when git was not asked: absent is not zero.
/// </summary>
public sealed record ReclaimableReport(bool Available, string Reason)
{
    /// <summary>Worktree candidates git confirmed; absent (not zero) when <see cref="Available"/> is false.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Tally? A24 { get; init; }

    /// <summary>Orphan candidates git's ignore rules confirmed (their tracked files were not checked); absent when <see cref="Available"/> is false.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Tally? A24Orphans { get; init; }

    /// <summary>The candidates that stay, by reason (a closed set of sentences), largest first; absent when <see cref="Available"/> is false.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RetainedGroup>? Retained { get; init; }

    /// <summary>How many candidates git could not be ASKED about (a refusal, a timeout, no git.exe): their bytes are in <see cref="Retained"/>,
    /// and while any exist a figure below the warning level is not "ok". Written only when above 0.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int NotAsked { get; init; }

    /// <summary>Why NO git could be asked (none admitted, not started, refused by the policy) — the distinct reasons; empty when git was asked. A scan fact
    /// <c>status</c> shows as "git could not be asked": while it is set the figures are absent, never zero, and the verdict is unknown.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GitUnavailable { get; init; }

    /// <summary>The confirmed bytes, worktrees and orphans together; 0 when nothing was confirmed or git was not asked (check <see cref="Available"/>).</summary>
    [JsonIgnore]
    public long ConfirmedBytes => (A24?.Bytes ?? 0) + (A24Orphans?.Bytes ?? 0);
}

/// <summary><c>worktrees.json</c> (PLAN_disk_hygiene_windows.md §3.3): the report <c>worktrees scan</c> writes under <c>%LOCALAPPDATA%\wsl-care</c>.</summary>
public sealed record WorktreeReport(
    int SchemaVersion,
    DateTimeOffset ScannedAt,
    double ElapsedSeconds,
    bool Complete,
    IReadOnlyList<RootReport> Roots,
    IReadOnlyList<TopFolder> Top,
    ReclaimableReport Reclaimable);
