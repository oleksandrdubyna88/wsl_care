namespace WslCare.Core.Worktrees;

/// <summary>The names <see cref="RootVerdict.Rule"/> takes — a closed set a client can key on.</summary>
public static class RootRule
{
    public const string Shape = "shape";
    public const string Missing = "missing";
    public const string NotAFolder = "not-a-folder";
    public const string LinkOnTheWay = "link-on-the-way";
    public const string NotLocalFixed = "not-a-local-fixed-drive";
    public const string DriveRoot = "drive-root";
    public const string Profile = "profile";
    public const string Protected = "protected-place";
    public const string Overlap = "overlap";

    /// <summary>The folder opens, but the system calls it by another name — an 8.3 short name, a <c>subst</c> drive, a trailing dot or space, a differently-cased link-free alias: it is judged under its real name, never under the spelling that hides it.</summary>
    public const string SpeltDifferently = "spelt-differently";
    public const string TooMany = "too-many";
}

/// <summary>One configured or proposed root, judged: accepted, or refused by ONE named rule with its sentence.</summary>
/// <param name="Given">The path as it was handed in.</param>
/// <param name="Accepted">Whether it may be a root.</param>
/// <param name="Path">The path in the one spelling the key stores (<c>D:\work</c>: backslashes, no trailing separator); empty when it has none.</param>
/// <param name="Rule">The refusing rule (<see cref="RootRule"/>); empty when accepted.</param>
/// <param name="Reason">The sentence; empty when accepted.</param>
public sealed record RootVerdict(string Given, bool Accepted, string Path, string Rule, string Reason);

/// <summary>What <c>worktrees roots set</c> answers on stdout: every path's own verdict, and whether anything was written — the list is
/// all-or-nothing, so one refusal leaves the key as it was and every path still gets its verdict.</summary>
/// <param name="SchemaVersion">The wire schema.</param>
/// <param name="Written">Whether the key was written (every path accepted).</param>
/// <param name="Roots">One verdict per path given, in the order given.</param>
public sealed record RootVerdictsReport(int SchemaVersion, bool Written, IReadOnlyList<RootVerdict> Roots);
