using WslCare.Core.Hosting;

namespace WslCare.Core.Files;

/// <summary>
/// Resolves a path to what it really names: every link in the chain followed, every <c>..</c>
/// applied to the REAL parent rather than the spelled one.
/// </summary>
/// <remarks>
/// <para>The deletion policy decides on this result, never on the path as typed. A lexical
/// normalisation would call <c>~/.local/link/../x</c> a child of <c>~/.local</c> while the kernel
/// resolves <c>link</c> first and lands wherever it points — which is exactly how a symlink or a
/// junction inside a cleanup root reaches a protected folder. <c>FileInfo.LinkTarget</c> reports a
/// link only on the component itself (measured 2026-10-02: a path THROUGH a junction reports none),
/// so the walk is component by component.</para>
/// <para>Pure: the disk is reached only through <c>linkTargetOf</c>, which answers the link target of
/// one existing path or <c>null</c> when it is not a link (or does not exist) — so the walk is a unit
/// test with a dictionary standing in for the disk, on both path families.</para>
/// </remarks>
public static class RealPath
{
    /// <summary>Mirrors the kernel's <c>ELOOP</c> guard: a cycle of links is an error, not a hang.</summary>
    public const int MaxLinkHops = 40;

    public static string Resolve(string absolutePath, PathRules rules, Func<string, string?> linkTargetOf)
    {
        var (prefix, segments) = rules.Split(absolutePath);
        var remaining = new Queue<string>(segments);
        var resolved = new List<string>();
        var hops = 0;
        while (remaining.Count > 0)
        {
            var segment = remaining.Dequeue();
            if (IsDot(segment) || PopIfDotDot(segment, resolved))
            {
                continue;
            }

            var target = linkTargetOf(Compose(prefix, [.. resolved, segment], rules));
            if (target is null)
            {
                resolved.Add(segment);
                continue;
            }

            hops = Hop(hops, absolutePath);
            (prefix, remaining) = Restart(prefix, resolved, target, remaining, rules);
            resolved.Clear();
        }

        return Compose(prefix, resolved, rules);
    }

    private static bool IsDot(string segment) => segment == ".";

    /// <summary><c>..</c> climbs from the REAL parent — the one already resolved — and never above the root.</summary>
    private static bool PopIfDotDot(string segment, List<string> resolved)
    {
        if (segment != "..")
        {
            return false;
        }

        if (resolved.Count > 0)
        {
            resolved.RemoveAt(resolved.Count - 1);
        }

        return true;
    }

    private static int Hop(int hops, string path) =>
        hops + 1 > MaxLinkHops
            ? throw new IOException($"too many levels of links while resolving {path}")
            : hops + 1;

    /// <summary>A link was met: the walk restarts from the target's root, with the target's segments
    /// in front of what was still to come, so links inside the target are followed too.</summary>
    private static (string Prefix, Queue<string> Remaining) Restart(
        string prefix, List<string> resolved, string target, Queue<string> remaining, PathRules rules)
    {
        var absoluteTarget = rules.IsAbsolute(target) ? target : rules.Join(Compose(prefix, resolved, rules), target);
        var (targetPrefix, targetSegments) = rules.Split(absoluteTarget);
        return (targetPrefix, new Queue<string>(targetSegments.Concat(remaining)));
    }

    private static string Compose(string prefix, IReadOnlyList<string> segments, PathRules rules) =>
        segments.Count == 0 ? prefix : prefix + string.Join(rules.Separator, segments);
}
