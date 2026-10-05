using WslCare.Core.Files;

namespace WslCare.Core.Agents;

/// <summary>What a session listing found: the sessions, and whether the listing reached its end.</summary>
public sealed record SessionScan(IReadOnlyList<(SessionName Session, DateTimeOffset LastWrite)> Sessions, bool Complete, string Note);

/// <summary>
/// The sessions of one layout (plan §15q D2): a glob of name patterns, one per folder level, matched over directory LISTINGS
/// — each level listed, the matching folders entered, the matching files of the last level taken with the length and last
/// write the listing gives. No file is opened; a link is never followed; a folder of a never-enter name (<c>memory</c>) is
/// never entered; a ceiling on entries and the caller's deadline bound the listing.
/// </summary>
public static class SessionGlob
{
    /// <summary>The most entries one layout's listing may see.</summary>
    public const int MaxEntries = 500_000;

    public static SessionScan Find(IFileSystem files, string under, string glob, IReadOnlySet<string> neverEnter, Func<bool> outOfTime)
    {
        var segments = glob.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var level = new List<(string Path, string Relative)> { (under, string.Empty) };
        var seen = 0;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var (next, listed) = Folders(files, level, segments[i], neverEnter);
            seen += listed;
            if (Stop(seen, outOfTime) is { Length: > 0 } why)
            {
                return new SessionScan([], false, why);
            }

            level = next;
        }

        return Sessions(files, level, segments[^1], seen, outOfTime);
    }

    /// <summary>Whether <paramref name="name"/> matches <paramref name="pattern"/>: <c>*</c> any run of characters, <c>?</c> one.</summary>
    public static bool Matches(string pattern, string name) => Matches(pattern.AsSpan(), name.AsSpan());

    private static bool Matches(ReadOnlySpan<char> pattern, ReadOnlySpan<char> name) => pattern.Length switch
    {
        0 => name.Length == 0,
        _ when pattern[0] == '*' => Matches(pattern[1..], name) || (name.Length > 0 && Matches(pattern, name[1..])),
        _ => name.Length > 0 && (pattern[0] == '?' || pattern[0] == name[0]) && Matches(pattern[1..], name[1..]),
    };

    private static (List<(string Path, string Relative)> Next, int Listed) Folders(IFileSystem files, List<(string Path, string Relative)> level, string pattern, IReadOnlySet<string> neverEnter)
    {
        var next = new List<(string, string)>();
        var listed = 0;
        foreach (var (path, relative) in level)
        {
            var entries = files.ListEntries(path);
            listed += entries.Count;
            next.AddRange(entries
                .Where(e => e.Kind == EntryKind.Directory && !neverEnter.Contains(e.Name) && Matches(pattern, e.Name))
                .Select(e => (Path.Combine(path, e.Name), relative + e.Name + "/")));
        }

        return (next, listed);
    }

    private static SessionScan Sessions(IFileSystem files, List<(string Path, string Relative)> level, string pattern, int seen, Func<bool> outOfTime)
    {
        var found = new List<(SessionName, DateTimeOffset)>();
        foreach (var (path, relative) in level)
        {
            var entries = files.ListEntries(path);
            seen += entries.Count;
            found.AddRange(entries.Where(e => e.Kind == EntryKind.File && Matches(pattern, e.Name)).Select(e => (new SessionName(relative + e.Name, e.Length), e.LastWriteUtc)));
            if (Stop(seen, outOfTime) is { Length: > 0 } why)
            {
                return new SessionScan(found, false, why);
            }
        }

        return new SessionScan(found, true, string.Empty);
    }

    private static string Stop(int seen, Func<bool> outOfTime) =>
        seen >= MaxEntries ? $"stopped after {MaxEntries} entries; the count is a lower bound"
        : outOfTime() ? "stopped at the walk's time budget; the count is a lower bound"
        : string.Empty;
}
