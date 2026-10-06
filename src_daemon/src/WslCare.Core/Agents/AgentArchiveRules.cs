namespace WslCare.Core.Agents;

/// <summary>
/// The shape rules of an <see cref="AgentArchiveBlock"/> (plan §15r, E9.S0) — pure, over the catalogue's own data, so a test can
/// hold the embedded catalogue to them and a planted entry proves they still bite. A problem is a sentence naming the agent.
/// </summary>
/// <remarks>The second net: a unit's glob, its companions and a file unit's glob are matched against the block's
/// <see cref="AgentArchiveBlock.NeverMove"/> names segment by segment. A LITERAL segment (no <c>*</c>, <c>?</c>, <c>{</c>) that a
/// never-move pattern matches is a unit that names something the archive must never move — a catalogue defect, refused here
/// before any code moves a byte. A wildcard segment cannot be judged by its spelling; the selection (E9.S1) applies the same
/// names to every concrete path it finds.</remarks>
public static class AgentArchiveRules
{
    private const string Placeholder = "{";

    /// <summary>Every problem of <paramref name="agent"/>'s archive block; empty when it has none or it is sound.</summary>
    public static IReadOnlyList<string> Problems(AgentEntry agent) => agent.Archive is not { } block
        ? []
        : [.. BlockProblems(agent, block), .. block.Units.SelectMany(u => UnitProblems(agent, block, u))];

    /// <summary>Whether <paramref name="relative"/> (a path below the layout's folder, <c>/</c>-separated) has a segment a
    /// never-move name of <paramref name="block"/> matches — or a segment named <see cref="AgentCatalogue.Memory"/>, in any case,
    /// which no agent ever moves (plan §15q H2).</summary>
    public static bool IsNeverMoved(AgentArchiveBlock block, string relative) =>
        relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => NeverMovedName(block, segment));

    private static bool NeverMovedName(AgentArchiveBlock block, string segment) =>
        string.Equals(segment, AgentCatalogue.Memory, StringComparison.OrdinalIgnoreCase) || block.NeverMove.Any(pattern => SessionGlob.Matches(pattern, segment));

    private static IEnumerable<string> BlockProblems(AgentEntry agent, AgentArchiveBlock block) =>
    [
        .. When(block.Units.Count == 0, $"{agent.Id}: an archive block with no unit"),
        .. When(!RetentionSources.All.Contains(block.Retention.Source, StringComparer.Ordinal), $"{agent.Id}: retention source \"{block.Retention.Source}\" is none of {string.Join(", ", RetentionSources.All)}"),
        .. When(block.Retention.Checked.Length == 0, $"{agent.Id}: the retention block does not say what was checked"),
        .. block.NeverMove.Where(p => !IsPlainName(p)).Select(p => $"{agent.Id}: the never-move name \"{p}\" is not one plain name (no /, \\, ..)"),
    ];

    private static IEnumerable<string> When(bool broken, string problem) => broken ? [problem] : [];

    private static IEnumerable<string> UnitProblems(AgentEntry agent, AgentArchiveBlock block, ArchiveUnit unit) => unit.Kind switch
    {
        ArchiveUnitKinds.Session => SessionUnitProblems(agent, block, unit),
        ArchiveUnitKinds.File => FileUnitProblems(agent, block, unit),
        _ => [$"{agent.Id}: unit kind \"{unit.Kind}\" is none of {string.Join(", ", ArchiveUnitKinds.All)}"],
    };

    private static IEnumerable<string> SessionUnitProblems(AgentEntry agent, AgentArchiveBlock block, ArchiveUnit unit)
    {
        if (agent.Sessions is not { } layout)
        {
            return [$"{agent.Id}: a session unit without a confirmed session layout"];
        }

        return unit.Glob.Length > 0
            ? [$"{agent.Id}: a session unit carries a glob of its own; one session is the layout's (plan §15q D2)"]
            : [.. Named(agent, block, [layout.Glob, .. layout.Companions, .. unit.SkipWhilePresent])];
    }

    private static IEnumerable<string> FileUnitProblems(AgentEntry agent, AgentArchiveBlock block, ArchiveUnit unit) =>
        agent.Sessions is null
            ? [$"{agent.Id}: a file unit needs the layout's folder to start in"]
            : unit.Glob.Length == 0 || !IsRelative(unit.Glob)
                ? [$"{agent.Id}: a file unit's glob \"{unit.Glob}\" is not a relative pattern"]
                : Named(agent, block, [unit.Glob]);

    /// <summary>The literal segments of <paramref name="templates"/> a never-move name matches.</summary>
    private static IEnumerable<string> Named(AgentEntry agent, AgentArchiveBlock block, IReadOnlyList<string> templates) =>
        templates.SelectMany(t => t.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(IsLiteral).Where(s => NeverMovedName(block, s))
            .Select(s => $"{agent.Id}: \"{t}\" names \"{s}\", which is never moved"));

    private static bool IsLiteral(string segment) => segment.IndexOfAny(['*', '?']) < 0 && !segment.Contains(Placeholder, StringComparison.Ordinal);

    private static bool IsPlainName(string pattern) =>
        pattern.Length > 0 && pattern.IndexOfAny(['/', '\\']) < 0 && pattern is not ("." or "..");

    private static bool IsRelative(string glob) =>
        !glob.StartsWith('/') && !glob.Contains('\\', StringComparison.Ordinal) && !glob.Split('/').Contains("..", StringComparer.Ordinal);
}
