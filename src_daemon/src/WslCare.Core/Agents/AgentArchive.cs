namespace WslCare.Core.Agents;

/// <summary>
/// One unit the archive moves as a whole (plan §15r D2, E9.S0): either the agent's SESSION — the session layout of
/// <see cref="AgentSessionLayout"/> with its companions, never redefined here (§15q D2) — or a FILE of its own, aged on its own
/// last write (Antigravity's <c>log/cli-&lt;timestamp&gt;.log</c>, one per CLI start, archive plan §3).
/// </summary>
/// <param name="Kind"><see cref="ArchiveUnitKinds.Session"/> or <see cref="ArchiveUnitKinds.File"/> — a closed set, held by a test.</param>
/// <param name="Glob">For a file unit: name patterns from the layout's folder, one per level; empty for a session unit (the
/// session layout's own glob applies).</param>
/// <param name="SkipWhilePresent">Companion templates (<c>{dir}</c>, <c>{id}</c> as in the layout) whose presence keeps the unit
/// where it is — a conversation database whose <c>-wal</c> exists may be open (§15r review M8, until the live gate decides).</param>
public sealed record ArchiveUnit(string Kind, string Glob, IReadOnlyList<string> SkipWhilePresent);

/// <summary>The names <see cref="ArchiveUnit.Kind"/> may take.</summary>
public static class ArchiveUnitKinds
{
    public const string Session = "session";

    public const string File = "file";

    public static IReadOnlyList<string> All { get; } = [Session, File];
}

/// <summary>Where an agent keeps its OWN retention — the deletion the archive must run ahead of (plan §15r D10).</summary>
/// <param name="Source"><see cref="RetentionSources.ClaudeSettings"/> (Claude Code's <c>cleanupPeriodDays</c>) or
/// <see cref="RetentionSources.None"/>.</param>
/// <param name="DefaultDays">The agent's documented default when its setting is absent; 0 when it deletes nothing on its own.</param>
/// <param name="Checked">What was checked to say so — a negative names its edges (planning rule: "checked: …, NOT checked: …").</param>
public sealed record AgentRetention(string Source, int DefaultDays, string Checked);

/// <summary>The names <see cref="AgentRetention.Source"/> may take.</summary>
public static class RetentionSources
{
    /// <summary>Claude Code's <c>cleanupPeriodDays</c> (managed settings, <c>CLAUDE_CONFIG_DIR</c>, <c>settings.json</c>; E9.S1 reads it).</summary>
    public const string ClaudeSettings = "claude-settings";

    /// <summary>No retention of its own is known.</summary>
    public const string None = "none";

    public static IReadOnlyList<string> All { get; } = [ClaudeSettings, None];
}

/// <summary>
/// An agent's <c>archive</c> block (plan §15r, archive plan §3): which units move, what must NEVER move — a second net behind
/// the units' globs, held by a test over the catalogue — and where the agent keeps its own retention. An agent without one is
/// never archived.
/// </summary>
/// <param name="Units">What moves, each unit as a whole.</param>
/// <param name="NeverMove">Name patterns (<c>*</c>, <c>?</c>) that no unit's literal name may match, at any level below the
/// layout's folder — the archive plan's "never moved" column.</param>
/// <param name="Retention">The agent's own retention.</param>
public sealed record AgentArchiveBlock(IReadOnlyList<ArchiveUnit> Units, IReadOnlyList<string> NeverMove, AgentRetention Retention);
