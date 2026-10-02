using WslCare.Core.Hosting;

namespace WslCare.Core.Files;

/// <summary>What the disk answered about ONE existing path component: a plain name, a link and its
/// target, or a component that could not be inspected at all.</summary>
/// <remarks>Three facts, kept apart on purpose: "not a link" and "could not look" are different, and
/// collapsing the second into the first is how a component we cannot see into gets treated as a
/// plain name (fail open). A missing component is a plain name — nothing exists there to follow.</remarks>
public abstract record LinkInspection
{
    private LinkInspection()
    {
    }

    public static readonly LinkInspection NotALink = new PlainName();

    public sealed record Link(string Target) : LinkInspection;

    public sealed record Uninspectable(string Reason) : LinkInspection;

    private sealed record PlainName : LinkInspection;
}

/// <summary>A path's real location, or why it could not be established.</summary>
public abstract record RealPathResult
{
    private RealPathResult()
    {
    }

    /// <summary>The real path: absolute, every link followed, <c>..</c> applied to the real parent.</summary>
    public sealed record Resolved(string Path) : RealPathResult;

    /// <summary>The walk stopped at <paramref name="Component"/>: it could not be inspected, or a cycle of links was met there.</summary>
    public sealed record Unresolvable(string Component, string Reason) : RealPathResult;
}

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
/// <para>Fail closed: a component that cannot be inspected, or a cycle of links, ends the walk as
/// <see cref="RealPathResult.Unresolvable"/> — never as a guess. Every destructive operation refuses
/// an unresolvable path.</para>
/// <para>Pure: the disk is reached only through <c>inspect</c>, which answers for one path — so the
/// walk is a unit test with a dictionary standing in for the disk, on both path families.</para>
/// </remarks>
public static class RealPath
{
    /// <summary>Mirrors the kernel's <c>ELOOP</c> guard: a cycle of links is an error, not a hang.</summary>
    public const int MaxLinkHops = 40;

    public static RealPathResult Resolve(string absolutePath, PathRules rules, Func<string, LinkInspection> inspect) =>
        new Walk(absolutePath, rules, inspect).Run();

    /// <summary>One resolution in progress: the prefix and segments already real, and what is still to come.</summary>
    private sealed class Walk
    {
        private readonly string _original;
        private readonly PathRules _rules;
        private readonly Func<string, LinkInspection> _inspect;
        private readonly List<string> _resolved = [];
        private string _prefix;
        private Queue<string> _remaining;
        private int _hops;

        public Walk(string original, PathRules rules, Func<string, LinkInspection> inspect)
        {
            _original = original;
            _rules = rules;
            _inspect = inspect;
            var (prefix, segments) = rules.Split(original);
            _prefix = prefix;
            _remaining = new Queue<string>(segments);
        }

        public RealPathResult Run()
        {
            while (_remaining.TryDequeue(out var segment))
            {
                if (Step(segment) is { } failure)
                {
                    return failure;
                }
            }

            return new RealPathResult.Resolved(Compose(_prefix, _resolved, _rules));
        }

        /// <summary>One segment: skipped, climbed, kept, or followed — or the reason the walk cannot go on.</summary>
        private RealPathResult.Unresolvable? Step(string segment)
        {
            if (IsDot(segment) || PopIfDotDot(segment, _resolved))
            {
                return null;
            }

            var component = Compose(_prefix, [.. _resolved, segment], _rules);
            return _inspect(component) switch
            {
                LinkInspection.Link link => Follow(component, link.Target),
                LinkInspection.Uninspectable failed => new RealPathResult.Unresolvable(component, failed.Reason),
                _ => Keep(segment),
            };
        }

        private RealPathResult.Unresolvable? Keep(string segment)
        {
            _resolved.Add(segment);
            return null;
        }

        /// <summary>A link was met: the walk restarts from the target's root, with the target's segments
        /// in front of what was still to come, so links inside the target are followed too.</summary>
        private RealPathResult.Unresolvable? Follow(string component, string target)
        {
            if (++_hops > MaxLinkHops)
            {
                return new RealPathResult.Unresolvable(component, $"too many levels of links (more than {MaxLinkHops}) while resolving {_original}");
            }

            var absoluteTarget = _rules.IsAbsolute(target) ? target : _rules.Join(Compose(_prefix, _resolved, _rules), target);
            var (targetPrefix, targetSegments) = _rules.Split(absoluteTarget);
            _prefix = targetPrefix;
            _remaining = new Queue<string>(targetSegments.Concat(_remaining));
            _resolved.Clear();
            return null;
        }
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

    private static string Compose(string prefix, IReadOnlyList<string> segments, PathRules rules) =>
        segments.Count == 0 ? prefix : prefix + string.Join(rules.Separator, segments);
}
