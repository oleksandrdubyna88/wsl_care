using WslCare.Core.Hosting;

namespace WslCare.Core.Files.Deletion;

/// <summary>
/// The ONE set of rules every delete and move is judged by (plan §15a C1), in the order they are
/// asked. Pure: paths arrive resolved, the answer is a <see cref="DeletionVerdict"/>.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><b>Agent memory.</b> Anything under an agent's <c>projects/*/memory/</c> — source or
/// destination — is refused unconditionally. The archive permit does not reach it.</item>
/// <item><b>The source.</b> Never under the Claude temp folder, never under <c>~/git</c>, never under
/// an AI agent's folder unless the scope holds the archive permit AND the operation is a move; and
/// otherwise it must be strictly inside the declared root.</item>
/// <item><b>The destination</b> of a move: never a protected place, and strictly inside the declared
/// root.</item>
/// <item><b>Root too broad</b>, asked just before either "inside the declared root" test: a declared
/// root that is a filesystem root or the home directory would make that test true of everything.
/// It comes after the protected-place rules so a refusal names the more specific reason.</item>
/// </list>
/// <para>"Strictly inside" means a proper descendant: an action may not delete its own root. A
/// protected root is protected together with itself: deleting <c>~/.claude</c> whole is as refused
/// as deleting a file inside it — and so are its ancestors: a folder that HOLDS a protected root is refused under that root's
/// rule, because a recursive delete or move would take the root with it (retro gate over PR #4).</para>
/// </remarks>
public sealed class DeletionPolicy(ProtectedRoots roots, PathRules rules)
{
    public DeletionVerdict Decide(DeletionRequest request)
    {
        if (IsAgentMemory(request.RealPath) || (request.Operation == FileOperation.Move && IsAgentMemory(request.RealDestination)))
        {
            return Refuse(DeletionRule.AgentMemory, request, "it is an AI agent's memory (projects/*/memory/), which is never moved or deleted");
        }

        var source = JudgeSource(request);
        return source.IsAllowed && request.Operation == FileOperation.Move ? JudgeDestination(request) : source;
    }

    /// <summary>The refusal for a path whose real location could not be established — asked by the file
    /// system before a request can even be formed, and worded here so every refusal reads the same.</summary>
    /// <param name="operation">Delete or move.</param>
    /// <param name="action">The scope's action name.</param>
    /// <param name="path">The path as the caller spelled it (its real path is the unknown).</param>
    /// <param name="component">Where the walk stopped.</param>
    /// <param name="reason">Why it stopped there.</param>
    public static DeletionVerdict Unresolvable(FileOperation operation, string action, string path, string component, string reason) =>
        Refuse(DeletionRule.Unresolvable, operation, action, path, $"its real path could not be established at {component} ({reason}); a path that cannot be inspected is never acted on");

    /// <summary>The refusal for a path that, checked again just before acting, resolved somewhere other than where it was approved.</summary>
    /// <param name="operation">Delete or move.</param>
    /// <param name="action">The scope's action name.</param>
    /// <param name="path">The path that was re-resolved.</param>
    /// <param name="nowResolvesTo">Where it resolves now.</param>
    public static DeletionVerdict Changed(FileOperation operation, string action, string path, string nowResolvesTo) =>
        Refuse(DeletionRule.PathChanged, operation, action, path, $"checked again just before acting it resolved to {nowResolvesTo}, not to the place that was approved; a link was swapped in after the decision");

    private DeletionVerdict JudgeSource(DeletionRequest request)
    {
        var path = request.RealPath;
        return NeverUnder(request, path)
            ?? HoldsProtectedRoot(request, path)
            ?? (Under(path, roots.AgentRoots) is { } agent ? JudgeAgentSource(request, agent) : JudgeInsideRoot(request, path));
    }

    private DeletionVerdict? NeverUnder(DeletionRequest request, string path)
    {
        if (Under(path, roots.ClaudeTempRoots) is { } temp)
        {
            return Refuse(DeletionRule.ClaudeTemp, request, $"it is under Claude Code's temp folder {temp}, which is never cleaned");
        }

        return Under(path, roots.GitRoots) is { } git
            ? Refuse(DeletionRule.GitFolder, request, $"it is under the repositories folder {git}; nothing under it is ever deleted")
            : null;
    }

    /// <summary>Retro gate over PR #4: a delete or a move is RECURSIVE, so a folder that holds a protected root takes that root
    /// with it — the never-list protects a root's ancestors as well as the root, under the same rule the root itself carries.</summary>
    private DeletionVerdict? HoldsProtectedRoot(DeletionRequest request, string path) =>
        Holding(path, roots.AgentRoots) is { } agent ? Refuse(DeletionRule.AgentFolder, request, $"it holds the AI agent folder {agent}, which would go with it")
        : Holding(path, roots.GitRoots) is { } git ? Refuse(DeletionRule.GitFolder, request, $"it holds the repositories folder {git}, which would go with it")
        : Holding(path, roots.ClaudeTempRoots) is { } temp ? Refuse(DeletionRule.ClaudeTemp, request, $"it holds Claude Code's temp folder {temp}, which would go with it")
        : null;

    /// <summary>Inside an agent's folder only the archive's MOVE is allowed, and only with the permit.</summary>
    private static DeletionVerdict JudgeAgentSource(DeletionRequest request, string agentRoot) =>
        request.Permit == DeletionPermit.MoveOutOfAgentFolder && request.Operation == FileOperation.Move
            ? DeletionVerdict.Allowed
            : Refuse(DeletionRule.AgentFolder, request, $"it is under the AI agent folder {agentRoot}; nothing under an agent's folder is ever deleted (plan §5)");

    private DeletionVerdict JudgeInsideRoot(DeletionRequest request, string path) =>
        TooBroad(request)
        ?? (rules.IsStrictlyUnder(path, request.RealRoot)
            ? DeletionVerdict.Allowed
            : Refuse(DeletionRule.OutsideDeclaredRoot, request, $"it is not strictly inside the declared root {request.RealRoot}"));

    private DeletionVerdict JudgeDestination(DeletionRequest request)
    {
        var destination = request.RealDestination;
        var protectedRoot = Under(destination, roots.AgentRoots) ?? Under(destination, roots.GitRoots) ?? Under(destination, roots.ClaudeTempRoots);
        if (protectedRoot is not null)
        {
            return Refuse(DeletionRule.ProtectedDestination, request, $"the destination {destination} is under the protected folder {protectedRoot}");
        }

        return TooBroad(request)
            ?? (rules.IsStrictlyUnder(destination, request.RealRoot)
                ? DeletionVerdict.Allowed
                : Refuse(DeletionRule.OutsideDeclaredRoot, request, $"the destination {destination} is not strictly inside the declared root {request.RealRoot}"));
    }

    /// <summary>A declared root that is a filesystem root or the home directory means nothing; refused before "inside the root" is even asked.</summary>
    private DeletionVerdict? TooBroad(DeletionRequest request) =>
        rules.IsRoot(request.RealRoot) || rules.PathEquals(request.RealRoot, roots.Home)
            ? Refuse(DeletionRule.RootTooBroad, request, $"the declared root {request.RealRoot} is a filesystem root or the home directory")
            : null;

    /// <summary>The first of <paramref name="candidates"/> that <paramref name="path"/> is, or is under.</summary>
    private string? Under(string path, IReadOnlyList<string> candidates) =>
        candidates.FirstOrDefault(root => rules.IsSameOrUnder(path, root));

    /// <summary>The first of <paramref name="candidates"/> that lies strictly under <paramref name="path"/>.</summary>
    private string? Holding(string path, IReadOnlyList<string> candidates) =>
        candidates.FirstOrDefault(root => rules.IsStrictlyUnder(root, path));

    /// <summary><c>…/projects/&lt;anything&gt;/memory</c> or below, anywhere in the path.</summary>
    private bool IsAgentMemory(string path)
    {
        if (path.Length == 0 || !rules.IsAbsolute(path))
        {
            return false;
        }

        var segments = rules.Segments(path);
        return Enumerable.Range(0, Math.Max(segments.Count - 2, 0))
            .Any(i => Same(segments[i], "projects") && Same(segments[i + 2], "memory"));
    }

    private bool Same(string a, string b) => string.Equals(a, b, rules.Comparison);

    private static DeletionVerdict Refuse(DeletionRule rule, DeletionRequest request, string why) =>
        Refuse(rule, request.Operation, request.Action, request.RealPath, why);

    private static DeletionVerdict Refuse(DeletionRule rule, FileOperation operation, string action, string path, string why) =>
        DeletionVerdict.Refuse(rule, $"{action}: refused to {Verb(operation)} {path}: {why}");

    private static string Verb(FileOperation operation) => operation == FileOperation.Move ? "move" : "delete";
}
