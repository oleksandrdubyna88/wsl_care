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
/// as deleting a file inside it.</para>
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

    private DeletionVerdict JudgeSource(DeletionRequest request)
    {
        var path = request.RealPath;
        if (Under(path, roots.ClaudeTempRoots) is { } temp)
        {
            return Refuse(DeletionRule.ClaudeTemp, request, $"it is under Claude Code's temp folder {temp}, which is never cleaned");
        }

        if (Under(path, roots.GitRoots) is { } git)
        {
            return Refuse(DeletionRule.GitFolder, request, $"it is under the repositories folder {git}; nothing under it is ever deleted");
        }

        return Under(path, roots.AgentRoots) is { } agent ? JudgeAgentSource(request, agent) : JudgeInsideRoot(request, path);
    }

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
        DeletionVerdict.Refuse(rule, $"{request.Action}: refused to {Verb(request.Operation)} {request.RealPath}: {why}");

    private static string Verb(FileOperation operation) => operation == FileOperation.Move ? "move" : "delete";
}
