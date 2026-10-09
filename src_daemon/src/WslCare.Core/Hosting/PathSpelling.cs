namespace WslCare.Core.Hosting;

/// <summary>
/// One rule for the segments of an absolute path a person types (E9.S1 review round B1), shared by every path key
/// (<c>Config.TextRule.AbsolutePathOrEmpty</c>) and a manual agent's data folder (<c>Agents.ExtraAgentShape</c>): no empty segment
/// (<c>//</c>), no <c>.</c> and no <c>..</c> after the root — <c>/</c>, <c>X:\</c> or <c>\\server\share</c> — and at most ONE
/// trailing separator. A spelling the kernel would resolve to another folder than it reads as is refused before anything compares
/// it with a mount table or a protected place.
/// </summary>
public static class PathSpelling
{
    public const string Says = "holds an empty, . or .. segment";

    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>Whether <paramref name="absolutePath"/> holds an empty, <c>.</c> or <c>..</c> segment after its root.</summary>
    public static bool HasBadSegment(string absolutePath)
    {
        var rest = AfterRoot(absolutePath);
        var trimmed = rest.Length > 0 && Separators.Contains(rest[^1]) ? rest[..^1] : rest;
        return trimmed.Length > 0 && trimmed.Split(Separators).Any(segment => segment is "" or "." or "..");
    }

    /// <summary>The path after its root: <c>/</c>, a drive's <c>X:\</c>, or a share's <c>\\server\share\</c>.</summary>
    private static string AfterRoot(string path) => path switch
    {
        _ when path.StartsWith(@"\\", StringComparison.Ordinal) => AfterShare(path),
        _ when IsDrive(path) => path[3..],
        _ when path.StartsWith('/') => path[1..],
        _ => path,
    };

    private static bool IsDrive(string path) => path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':';

    private static string AfterShare(string path)
    {
        var server = path.IndexOfAny(Separators, 2);
        var share = server < 0 ? -1 : path.IndexOfAny(Separators, server + 1);
        return share < 0 ? string.Empty : path[(share + 1)..];
    }
}
