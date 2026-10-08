using WslCare.Core.Actions;
using WslCare.Core.Config;

namespace WslCare.Core.Archive;

/// <summary>An agent's own retention as the preview found it: <paramref name="Known"/> with its <paramref name="Days"/>, or not known
/// (E9.S1 review round m8: <paramref name="Days"/> is then 0 and means nothing).</summary>
public sealed record ArchiveRetentionReport(string Source, bool Known, int Days, string From, IReadOnlyList<string> Warnings);

/// <summary>One unit the archive would move — a live answer, names included (never persisted).</summary>
public sealed record ArchiveUnitReport(string Kind, string Key, int Files, long Bytes, DateTimeOffset NewestWriteUtc, string Month);

/// <summary>How many due units one rule keeps in place, and the first of them with its sentence.</summary>
public sealed record SkipCount(string Rule, int Count, string FirstKey, string FirstWhy);

/// <summary>What the open-file check saw: <paramref name="State"/> <c>complete</c>, <c>cut</c> or <c>not-checked</c>.</summary>
public sealed record InUseReport(string State, int OpenFiles, int ClaudeProjects, string Note);

/// <summary>One agent of <c>archive preview</c>.</summary>
/// <param name="Enabled">Whether <c>archive.agents</c> holds it; an agent asked for alone is previewed, never moved.</param>
/// <param name="EffectiveAgeDays">The age a unit is due at (§15r D10).</param>
/// <param name="OldestDueWriteUtc">The newest write of the oldest due unit — how close the agent's own deletion is.</param>
/// <param name="Units">The first <c>preview.maxItems</c> due units that may move, oldest first.</param>
public sealed record AgentPreviewReport(
    string Id,
    string Name,
    bool Enabled,
    string Under,
    ArchiveRetentionReport Retention,
    int EffectiveAgeDays,
    int DueUnits,
    int DueFiles,
    long DueBytes,
    DateTimeOffset? OldestDueWriteUtc,
    int Younger,
    IReadOnlyList<SkipCount> Skipped,
    int Quarantined,
    string Note,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ArchiveUnitReport> Units);

/// <summary>
/// <c>archive preview [--agent &lt;id&gt;] --json</c> (plan §15r E9.S1): what the archive would move on this side now, per agent — read
/// from listings and stats; nothing is opened but the agent's retention setting, nothing is written.
/// </summary>
/// <param name="SideFolder">The side folder the units would go under (§15r D4: <c>windows-&lt;host&gt;</c>, <c>wsl-&lt;host&gt;-&lt;distro&gt;</c>).</param>
/// <param name="Zone">The time zone the months are in.</param>
/// <param name="BaseFolder">The configured base; empty when none is — the preview still answers.</param>
public sealed record ArchivePreviewReport(
    int SchemaVersion,
    string Side,
    string SideFolder,
    DateTimeOffset AnsweredAt,
    string Zone,
    string BaseFolder,
    InUseReport InUse,
    IReadOnlyList<AgentPreviewReport> Agents)
{
    /// <summary>The sessions this side archived at least <c>archive.removeAfterHours</c> ago, waiting for a run to remove their source
    /// (plan §15r E9.S4: A13's trigger fires on these too) — from the local in-flight file only; the base is never read.</summary>
    public int RemovalsDue { get; init; }
}

/// <summary>Builds <see cref="ArchivePreviewReport"/> from a selection.</summary>
public static class ArchivePreview
{
    /// <summary>The share of the preview's ceiling its listing may spend; the rest is the answer's (a property of a ceiling, like the
    /// 60 s a command keeps under its own — group C).</summary>
    public const double ListingShare = 0.75;

    /// <summary>E9.S1 review round m1: the time the preview's listing — the open-file scan, the layouts, every companion walk — may take,
    /// DERIVED from <c>archive.previewTimeoutSeconds</c> (the ceiling the preview runs under), never coupled to another key.</summary>
    public static TimeSpan ListingBudget(EffectiveConfig config) => TimeSpan.FromSeconds(config.Int(ConfigKeys.Archive.PreviewTimeoutSeconds) * ListingShare);

    public static ArchivePreviewReport From(SelectionInput input, IReadOnlyList<AgentSelection> selected, string sideFolder) =>
        new(
            SchemaVersion.Current,
            input.Paths.Side == Hosting.HostSide.Wsl ? "wsl" : "windows",
            sideFolder,
            input.Now,
            input.Zone.Id,
            input.Config.Text(ConfigKeys.Archive.BaseFolder),
            InUseOf(input.InUse),
            [.. selected.Select(s => Agent(input.Config, s))])
        {
            RemovalsDue = RemovalsDueOf(input),
        };

    /// <summary>The in-flight entries archived and past <c>archive.removeAfterHours</c> — what the next run's phase 2 takes.</summary>
    private static int RemovalsDueOf(SelectionInput input)
    {
        var after = TimeSpan.FromHours(input.Config.Int(ConfigKeys.Archive.RemoveAfterHours));
        return new ArchiveState(input.Paths, input.Files).Inflight().Entries.Count(e => e.State == InflightStates.Archived && input.Now - e.ArchivedAtUtc >= after);
    }

    /// <summary>What the open-file check saw, as an answer reports it (the preview's and the run's).</summary>
    public static InUseReport InUseOf(InUseView view) => new(StateName(view.State), view.OpenFiles.Count, view.ClaudeProjects.Count, view.Note);

    private static string StateName(InUseState state) => state switch
    {
        InUseState.Complete => "complete",
        InUseState.Cut => "cut",
        _ => "not-checked",
    };

    private static AgentPreviewReport Agent(EffectiveConfig config, AgentSelection s) =>
        new(
            s.Entry.Id,
            s.Entry.Name,
            s.Enabled,
            s.Under,
            Retention(s),
            s.EffectiveAgeDays,
            s.Due.Count,
            s.Due.Sum(u => u.Files.Count),
            s.Due.Sum(u => u.Bytes),
            s.Due.Concat(s.Skipped).Select(u => (DateTimeOffset?)u.NewestWriteUtc).Min(),
            s.Younger,
            [.. s.Skipped.GroupBy(u => u.SkipRule, StringComparer.Ordinal).Select(g => new SkipCount(g.Key, g.Count(), g.First().Key, g.First().Skip))],
            s.Quarantined,
            s.Note,
            Warnings(config, s),
            [.. s.Due.Take(ActionPreview.MaxItems).Select(u => new ArchiveUnitReport(u.Kind, u.Key, u.Files.Count, u.Bytes, u.NewestWriteUtc, u.Month))]);

    private static ArchiveRetentionReport Retention(AgentSelection s) => s.Retention is RetentionFound.Known known
        ? new ArchiveRetentionReport(s.Entry.Archive!.Retention.Source, true, known.Days, known.From, known.Warnings)
        : new ArchiveRetentionReport(s.Entry.Archive!.Retention.Source, false, 0, s.Retention.From, s.Retention.Warnings);

    /// <summary>The retention's own warnings, and — when it shortened the age — that the agent's own deletion set it.</summary>
    private static IReadOnlyList<string> Warnings(EffectiveConfig config, AgentSelection s)
    {
        var older = config.Int(ConfigKeys.Archive.OlderThanDays);
        return
        [
            .. s.Enabled ? Array.Empty<string>() : [$"{s.Entry.Id} is not in {ConfigKeys.Archive.Agents.Name}: previewed only — the archive does not move it until {ConfigKeys.Archive.Agents.Name} holds it"],
            .. s.Retention.Warnings,
            .. s.EffectiveAgeDays < older && s.Retention is RetentionFound.Known known
                ? [$"{s.Entry.Name} deletes its own sessions after {known.Days} days ({known.From}): the archive takes them from day {s.EffectiveAgeDays}, not {ConfigKeys.Archive.OlderThanDays.Name} ({older})"]
                : Array.Empty<string>(),
        ];
    }
}
