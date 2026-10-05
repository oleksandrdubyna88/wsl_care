using WslCare.Core.Actions.UserCaches;
using WslCare.Core.Agents;
using WslCare.Core.Files;

namespace WslCare.Core.Actions;

/// <summary>
/// Plan §15q R2 (review B2): an action whose declared cleanup folder (<see cref="ICleanupAction.HomeRoots"/>) is, sits inside or
/// contains an AI agent's folder — a catalogue one or a manual one, accepted or not — REFUSES, with the overlap named. A
/// command-based cleanup (npm, pnpm, pip, uv, dotnet) cannot be policed by the deletion policy, so the overlap is refused
/// before the action runs; "nothing in an agent folder is deleted" holds for commands too.
/// </summary>
public static class AgentFolderOverlap
{
    /// <summary>Why <paramref name="action"/> may not run in <paramref name="context"/>; empty when no folder overlaps.</summary>
    public static string Refusal(ICleanupAction action, ActionContext context)
    {
        var home = CacheFolders.Home(context);
        if (action.HomeRoots.Count == 0 || home.Length == 0)
        {
            return string.Empty;
        }

        var agents = context.Paths.AgentRoots.Select(r => (Spelt: r, Real: RealOrFull(context.Files, r))).ToList();
        return action.HomeRoots
            .SelectMany(root => Clashes(root, RealOrFull(context.Files, root.Under(home, context.Paths.Rules)), agents))
            .FirstOrDefault() ?? string.Empty;
    }

    /// <summary>Why a folder an action works in — <paramref name="display"/>, seen here as <paramref name="folder"/> — may not be
    /// cleaned: it is, sits inside or holds an AI agent's folder; empty when it does not (review S4: the folder a TOOL says it uses).</summary>
    public static string Refusal(ActionContext context, string folder, string display)
    {
        var real = RealOrFull(context.Files, folder);
        return context.Paths.AgentRoots.Select(r => (Spelt: r, Real: RealOrFull(context.Files, r)))
            .Where(a => ExtraAgentRules.Overlaps(real, a.Real))
            .Select(a => $"the cache folder {display} overlaps the AI agent folder {a.Spelt}, under which nothing is ever deleted (plan §15q R2) — refused")
            .FirstOrDefault() ?? string.Empty;
    }

    private static IEnumerable<string> Clashes(HomeFolder root, string real, IReadOnlyList<(string Spelt, string Real)> agents) =>
        agents.Where(a => ExtraAgentRules.Overlaps(real, a.Real))
            .Select(a => $"its cleanup folder {root.Display} overlaps the AI agent folder {a.Spelt}, under which nothing is ever deleted (plan §15q R2) — refused");

    private static string RealOrFull(IFileSystem files, string path) =>
        files.ResolvePath(path) is RealPathResult.Resolved r ? r.Path : Path.GetFullPath(path);
}
