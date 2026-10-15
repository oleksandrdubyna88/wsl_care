using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Worktrees;

/// <summary>
/// The rules a worktree root must pass (E19.S3, PLAN_disk_hygiene_windows.md §3.3 "Roots", E19 stories §3 decision 3), judged at
/// <c>worktrees roots set</c> AND again at the start of every scan — exactly as the archive's base folder is: a value that held once is not
/// trusted. In order, for each path: a drive-letter path; on a local FIXED drive; not a drive root; an existing folder; no link on any
/// component; not the profile or an ancestor of it; not inside, or around, an AI agent's folder, Claude's temporary folder or the
/// product's own folders. Then, across the list in order, an EARLIER root wins: a root inside or over an earlier one (or the same one
/// again) is refused, and so is every root past <c>worktrees.maxRoots</c>. Nested or overlapping roots would count twice and could delete twice.
/// </summary>
public static class WorktreeRoots
{
    /// <summary>The kind of the drive the letter names on THIS machine (<see cref="DriveInfo"/>); <see cref="DriveType.Unknown"/> when it cannot be asked.</summary>
    public static DriveType ThisMachinesDrive(char letter)
    {
        try
        {
            return new DriveInfo(letter.ToString()).DriveType;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return DriveType.Unknown;
        }
    }

    /// <summary>One verdict per path given, in the order given.</summary>
    public static IReadOnlyList<RootVerdict> Judge(IHostPaths paths, IFileSystem files, Func<char, DriveType> driveOf, int maxRoots, IReadOnlyList<string> given)
    {
        var judged = new List<RootVerdict>();
        foreach (var path in given)
        {
            judged.Add(AcrossTheList(paths.Rules, JudgeOne(paths, files, driveOf, path), judged, maxRoots));
        }

        return judged;
    }

    /// <summary>An accepted root checked against the accepted roots before it: overlap, then the count.</summary>
    private static RootVerdict AcrossTheList(PathRules rules, RootVerdict single, IReadOnlyList<RootVerdict> before, int maxRoots)
    {
        var accepted = before.Where(v => v.Accepted).ToList();
        return !single.Accepted ? single
            : accepted.FirstOrDefault(v => Overlap(rules, single.Path, v.Path)) is { } clash ? Refuse(single, RootRule.Overlap, $"{single.Path} is, holds or lies inside {clash.Path}, a root before it; nested or overlapping roots would count twice")
            : accepted.Count >= maxRoots ? Refuse(single, RootRule.TooMany, $"at most {maxRoots} roots are allowed (worktrees.maxRoots); {single.Path} is past them")
            : single;
    }

    private static bool Overlap(PathRules rules, string a, string b) => rules.IsSameOrUnder(a, b) || rules.IsStrictlyUnder(b, a);

    private static RootVerdict Refuse(RootVerdict verdict, string rule, string reason) => verdict with { Accepted = false, Rule = rule, Reason = reason };

    private static RootVerdict JudgeOne(IHostPaths paths, IFileSystem files, Func<char, DriveType> driveOf, string given)
    {
        var path = Canonical(given);
        var refusal = ShapeProblem(path) ?? DriveProblem(paths.Rules, driveOf, path) ?? DiskProblem(paths.Rules, files, path) ?? PlaceProblem(paths, files, path);
        return refusal is { } problem
            ? new RootVerdict(given, false, string.Empty, problem.Rule, problem.Why)
            : new RootVerdict(given, true, path, string.Empty, string.Empty);
    }

    /// <summary>The one spelling the key stores: backslashes, no trailing separator (except a drive root's), the drive letter upper case.</summary>
    internal static string Canonical(string given)
    {
        var text = given.Trim().Replace('/', '\\');
        text = text.Length > 3 && text.EndsWith('\\') ? text[..^1] : text;
        return text.Length >= 1 && char.IsAsciiLetterLower(text[0]) ? char.ToUpperInvariant(text[0]) + text[1..] : text;
    }

    private static (string Rule, string Why)? ShapeProblem(string path) =>
        new TextRule.WindowsLocalPath().Problem(path) is { Length: > 0 } problem ? (RootRule.Shape, $"{(path.Length == 0 ? "an empty path" : path)}: {problem}") : null;

    /// <summary>A local FIXED drive, and not the drive's root.</summary>
    private static (string Rule, string Why)? DriveProblem(PathRules rules, Func<char, DriveType> driveOf, string path) =>
        driveOf(path[0]) is var kind && kind != DriveType.Fixed ? (RootRule.NotLocalFixed, $"{path[0]}: is a {kind} drive, not a local fixed one")
        : rules.IsRoot(path) ? (RootRule.DriveRoot, $"{path} is the root of a drive; choose a folder inside it")
        : null;

    private static (string Rule, string Why)? DiskProblem(PathRules rules, IFileSystem files, string path) =>
        files.DirectoryExists(path) ? LinkProblem(rules, files, path)
        : files.FileExists(path) ? (RootRule.NotAFolder, $"{path} is a file, not a folder")
        : (RootRule.Missing, $"{path} does not exist; a root is never created — create it, then choose it");

    /// <summary>Its real path must be its spelling: a link anywhere on the way would let what the root IS change after it was judged.</summary>
    private static (string Rule, string Why)? LinkProblem(PathRules rules, IFileSystem files, string path) => files.ResolvePath(path) switch
    {
        RealPathResult.Resolved real when rules.PathEquals(real.Path, path) => null,
        RealPathResult.Resolved real => (RootRule.LinkOnTheWay, $"{path} is reached through a link (it leads to {real.Path}); choose the folder it leads to"),
        RealPathResult.Unresolvable unresolved => (RootRule.LinkOnTheWay, $"{path} cannot be inspected for a link at {unresolved.Component} ({unresolved.Reason})"),
        _ => throw new System.Diagnostics.UnreachableException("RealPathResult is a closed set"),
    };

    private static (string Rule, string Why)? PlaceProblem(IHostPaths paths, IFileSystem files, string path)
    {
        var rules = paths.Rules;
        var real = RealOrSelf(files, path);
        var home = RealOrSelf(files, paths.Home);
        if (rules.PathEquals(real, home) || rules.IsStrictlyUnder(home, real))
        {
            return (RootRule.Profile, $"{path} is, or holds, your profile {paths.Home}; choose a folder inside it");
        }

        return ProtectedPlaces(paths).Where(p => Overlap(rules, real, RealOrSelf(files, p.Path))).Select(p => p.Whose).FirstOrDefault() is { Length: > 0 } whose
            ? (RootRule.Protected, $"{path} is, holds or lies inside {whose}, which a cleanup never touches")
            : null;
    }

    private static string RealOrSelf(IFileSystem files, string path) => files.ResolvePath(path) is RealPathResult.Resolved real ? real.Path : path;

    private static IEnumerable<(string Path, string Whose)> ProtectedPlaces(IHostPaths paths) =>
    [
        .. paths.AgentRoots.Select(r => (r, $"the AI agent folder {r}")),
        .. paths.ClaudeTempRoots.Select(r => (r, $"Claude's temporary folder {r}")),
        (paths.StateDirectory, $"wsl-care's own folder {paths.StateDirectory}"),
        (paths.LogDirectory, $"wsl-care's own folder {paths.LogDirectory}"),
    ];
}
